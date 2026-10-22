"""JP-Vision supervised binary segmentation batch job; no cloud-account operations."""
from __future__ import annotations

import argparse
import json
import math
import os
from pathlib import Path
import random
import signal
import sys
import time
from typing import Any
import uuid

from dataset import Dataset, Sample, file_sha256, metrics, tile_starts, validate_dataset


class TrainingStopped(Exception):
    pass


class StopControl:
    def __init__(self, minutes: float):
        self.deadline = time.monotonic() + minutes * 60
        self.requested = False

    def request(self, *_: Any) -> None:
        self.requested = True

    def check(self) -> None:
        if self.requested or time.monotonic() >= self.deadline:
            raise TrainingStopped("Stop requested or --max-minutes reached; the rented instance still needs stopping.")


def write_json(path: Path, value: Any) -> None:
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    temporary.write_text(json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + "\n", encoding="utf-8")
    os.replace(temporary, path)


def torch_modules():
    # Lazy imports keep dataset validation usable without downloading Torch/CUDA.
    import numpy as np
    from PIL import Image
    import torch
    from torch import nn
    import torch.nn.functional as functional
    return np, Image, torch, nn, functional


def create_model():
    _, _, torch, nn, functional = torch_modules()

    def block(channels_in: int, channels_out: int):
        return nn.Sequential(nn.Conv2d(channels_in, channels_out, 3, padding=1), nn.ReLU(),
                             nn.Conv2d(channels_out, channels_out, 3, padding=1), nn.ReLU())

    class SmallUNet(nn.Module):
        def __init__(self):
            super().__init__()
            self.first = block(3, 16)
            self.second = block(16, 32)
            self.bottom = block(32, 64)
            self.up_second = block(96, 32)
            self.up_first = block(48, 16)
            self.head = nn.Conv2d(16, 1, 1)

        def forward(self, image):
            a = self.first(image)
            b = self.second(functional.max_pool2d(a, 2))
            c = self.bottom(functional.max_pool2d(b, 2))
            d = self.up_second(torch.cat((functional.interpolate(c, scale_factor=2, mode="bilinear", align_corners=False), b), 1))
            e = self.up_first(torch.cat((functional.interpolate(d, scale_factor=2, mode="bilinear", align_corners=False), a), 1))
            return self.head(e)

    return SmallUNet()


class PatchDataset:
    """One decoded image/mask pair is cached; no full dataset or resized full images."""
    def __init__(self, samples: tuple[Sample, ...], size: int, overlap: int):
        self.samples, self.size = samples, size
        self.tiles: list[tuple[int, int, int]] = []
        self.cached_index = -1
        self.cached = None
        for i, sample in enumerate(samples):
            xs, ys = tile_starts(sample.width, size, overlap), tile_starts(sample.height, size, overlap)
            if len(xs) * len(ys) > 4096:
                raise ValueError("More than 4096 tiles in one image; reduce overlap or crop a relevant ROI.")
            self.tiles.extend((i, x, y) for y in ys for x in xs)
            if len(self.tiles) > 1000000:
                raise ValueError("Training dataset exceeds one million tile references.")

    def __len__(self):
        return len(self.tiles)

    def __getitem__(self, index):
        np, Image, torch, _, _ = torch_modules()
        sample_index, x, y = self.tiles[index]
        sample = self.samples[sample_index]
        if self.cached_index != sample_index:
            # Recheck pinned bytes once per decoded image, protecting against edits after validation.
            if file_sha256(sample.image) != sample.image_hash or file_sha256(sample.mask) != sample.mask_hash:
                raise ValueError("Dataset files changed after validation.")
            with Image.open(sample.image) as source, Image.open(sample.mask) as mask:
                self.cached = (source.convert("RGB"), mask.copy())
            self.cached_index = sample_index
        image, mask = self.cached
        box = (x, y, x + self.size, y + self.size)
        # PIL crops pad with zero; validity masks keep padded pixels out of the loss.
        pixels = np.asarray(image.crop(box), dtype=np.float32).transpose(2, 0, 1).copy() / 255.0
        target = np.asarray(mask.crop(box), dtype=np.float32).copy()[None] / 255.0
        valid = np.zeros((1, self.size, self.size), dtype=np.float32)
        valid[:, :min(self.size, sample.height - y), :min(self.size, sample.width - x)] = 1
        return torch.from_numpy(pixels), torch.from_numpy(target), torch.from_numpy(valid)


def evaluate(model, samples: tuple[Sample, ...], args, device, control: StopControl) -> dict[str, Any]:
    np, Image, torch, _, _ = torch_modules()
    model.eval()
    tp = fp = fn = clean_images = false_positive_images = 0
    with torch.inference_mode():
        for sample in samples:
            control.check()
            patches = PatchDataset((sample,), args.tile_size, args.overlap)
            sums = np.zeros((sample.height, sample.width), dtype=np.float32)
            counts = np.zeros((sample.height, sample.width), dtype=np.float32)
            for index, (_, x, y) in enumerate(patches.tiles):
                control.check()
                image, _, _ = patches[index]
                score = model(image[None].to(device)).sigmoid()[0, 0].cpu().numpy()
                if not np.isfinite(score).all():
                    raise ValueError("Nonfinite model scores during validation.")
                width, height = min(args.tile_size, sample.width - x), min(args.tile_size, sample.height - y)
                sums[y:y + height, x:x + width] += score[:height, :width]
                counts[y:y + height, x:x + width] += 1
            if not (counts > 0).all():
                raise ValueError("Evaluation tiling left unmeasured pixels.")
            prediction = sums / counts >= args.threshold
            with Image.open(sample.mask) as mask:
                target = np.asarray(mask) == 255
            tp += int(np.count_nonzero(prediction & target))
            fp += int(np.count_nonzero(prediction & ~target))
            fn += int(np.count_nonzero(~prediction & target))
            if not target.any():
                clean_images += 1
                false_positive_images += int(prediction.any())
    return {**metrics(tp, fp, fn), "threshold": args.threshold, "images": len(samples),
            "cleanImages": clean_images, "falsePositiveImages": false_positive_images,
            "falsePositiveImagePolicy": "any positive pixel; runtime area filtering not applied",
            "tileSize": args.tile_size, "overlap": args.overlap, "stitch": "uniform mean"}


def export_bundle(model, args, output: Path, dataset: Dataset, control: StopControl) -> dict[str, Any]:
    np, _, torch, nn, _ = torch_modules()
    import onnx
    import onnxruntime as ort

    class ProbabilityModel(nn.Module):
        def __init__(self, inner):
            super().__init__()
            self.inner = inner

        def forward(self, image):
            return torch.sigmoid(self.inner(image))

    control.check()
    wrapper = ProbabilityModel(model.cpu().eval())
    generator = torch.Generator().manual_seed(981)
    probes = (torch.zeros(1, 3, args.tile_size, args.tile_size),
              torch.ones(1, 3, args.tile_size, args.tile_size),
              torch.rand(1, 3, args.tile_size, args.tile_size, generator=generator))
    # Fixed shape and embedded weights match native single-tensor deployment contract.
    staging = output / ("bundle-" + uuid.uuid4().hex + ".tmp")
    staging.mkdir()
    model_path = staging / "model.onnx"
    torch.onnx.export(wrapper, probes[0], str(model_path), input_names=["image"], output_names=["scores"],
                      opset_version=17, dynamo=False, external_data=False)
    control.check()
    onnx.checker.check_model(str(model_path), full_check=True)
    session = ort.InferenceSession(str(model_path), providers=["CPUExecutionProvider"])
    expected_input, expected_output = [1, 3, args.tile_size, args.tile_size], [1, 1, args.tile_size, args.tile_size]
    if len(session.get_inputs()) != 1 or len(session.get_outputs()) != 1 or session.get_inputs()[0].shape != expected_input or session.get_outputs()[0].shape != expected_output:
        raise ValueError("Exported tensor signature differs from native deployment contract.")
    errors = []
    with torch.inference_mode():
        for image in probes:
            control.check()
            expected = wrapper(image).numpy()
            actual = session.run(["scores"], {"image": image.numpy()})[0]
            if not np.isfinite(actual).all() or actual.min() < 0 or actual.max() > 1:
                raise ValueError("Exported model produces invalid probability scores.")
            np.testing.assert_allclose(actual, expected, rtol=1e-4, atol=1e-5)
            errors.append(float(np.max(np.abs(actual - expected))))
    manifest = {"schemaVersion": 1, "modelId": args.model_id, "version": args.model_version,
                "task": "binarySegmentation", "modelFile": "model.onnx", "modelSha256": file_sha256(model_path),
                "input": {"name": "image", "width": args.tile_size, "height": args.tile_size, "channels": 3,
                          "layout": "NCHW", "dtype": "float32", "color": "RGB", "scale": 1 / 255,
                          "mean": [0, 0, 0], "std": [1, 1, 1]},
                "output": {"name": "scores", "layout": "NCHW", "dtype": "float32", "activation": "none", "scoreKind": "probability"},
                "labels": ["background", "defect"]}
    write_json(staging / "manifest.json", manifest)
    parity = {"onnxChecker": "passed", "cpuParity": "passed", "probeCount": len(probes),
              "maxAbsoluteErrors": errors, "rtol": 1e-4, "atol": 1e-5,
              "torchVersion": torch.__version__, "onnxRuntimeVersion": ort.__version__,
              "datasetSha256": dataset.sha256, "accuracyClaim": "none; numerical export parity is not defect accuracy"}
    write_json(staging / "export-validation.json", parity)
    control.check()
    os.replace(staging, output / "bundle")
    return parity


def run_training(args, dataset: Dataset) -> int:
    np, _, torch, _, functional = torch_modules()
    output = args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    if any(output.iterdir()):
        raise ValueError("Output directory must be empty; use a new output directory when resuming.")
    control = StopControl(args.max_minutes)
    signal.signal(signal.SIGTERM, control.request)
    signal.signal(signal.SIGINT, control.request)
    random.seed(args.seed)
    np.random.seed(args.seed)
    torch.manual_seed(args.seed)
    torch.set_num_threads(2)
    device = torch.device(args.device)
    if args.device == "cuda":
        if not torch.cuda.is_available():
            raise ValueError("CUDA is unavailable; verify rental GPU passthrough and the driver before training.")
        # Force a real kernel/host synchronization before spending time reading all patches.
        torch.ones(1, device=device).sum().item()
    model = create_model().to(device)
    optimizer = torch.optim.Adam(model.parameters(), lr=args.learning_rate)
    patch_dataset = PatchDataset(dataset.splits["train"], args.tile_size, args.overlap)
    loader = torch.utils.data.DataLoader(patch_dataset, batch_size=args.batch_size, shuffle=True,
                                         num_workers=0, pin_memory=args.device == "cuda")
    epoch = step = 0
    best_dice = -1.0
    best_state = None
    history: list[dict[str, Any]] = []
    training_settings = {"overlap": args.overlap, "batchSize": args.batch_size,
                         "positiveWeight": args.positive_weight, "learningRate": args.learning_rate,
                         "threshold": args.threshold, "seed": args.seed}
    if args.resume:
        # Explicit safe mode; checkpoints contain tensors/primitive metadata only.
        saved = torch.load(args.resume, map_location="cpu", weights_only=True)
        if saved["datasetSha256"] != dataset.sha256 or saved["tileSize"] != args.tile_size:
            raise ValueError("Checkpoint dataset hash or tile size differs.")
        if saved.get("trainingSettings") != training_settings:
            raise ValueError("Checkpoint training settings differ (overlap, batch size, loss, threshold or seed).")
        model.load_state_dict(saved["model"])
        optimizer.load_state_dict(saved["optimizer"])
        epoch, step, best_dice = saved["completedEpochs"], saved["steps"], saved["bestDice"]
        best_state = saved["bestModel"]
        torch.set_rng_state(saved["torchRngState"])
    write_json(output / "dataset-audit.json", dataset.audit)
    write_json(output / "run-config.json", {key: str(value) if isinstance(value, Path) else value for key, value in vars(args).items()})

    def checkpoint():
        temporary = output / "checkpoint.tmp"
        torch.save({"formatVersion": 1, "datasetSha256": dataset.sha256, "tileSize": args.tile_size,
                    "trainingSettings": training_settings,
                    "completedEpochs": epoch, "steps": step, "model": model.state_dict(),
                    "optimizer": optimizer.state_dict(), "bestDice": best_dice, "bestModel": best_state,
                    "torchRngState": torch.get_rng_state()}, temporary)
        os.replace(temporary, output / "checkpoint.pt")

    report = {"status": "running", "datasetSha256": dataset.sha256, "trainingTiles": len(patch_dataset),
              "device": str(device), "torchVersion": torch.__version__, "history": history}
    try:
        positive_weight = torch.tensor(args.positive_weight, device=device)
        while epoch < args.epochs:
            control.check()
            model.train()
            total_loss, batches = 0.0, 0
            for image, target, valid in loader:
                control.check()
                image, target, valid = image.to(device), target.to(device), valid.to(device)
                optimizer.zero_grad(set_to_none=True)
                logits = model(image)
                bce = (functional.binary_cross_entropy_with_logits(logits, target, reduction="none", pos_weight=positive_weight) * valid).sum() / valid.sum()
                probability = logits.sigmoid() * valid
                dice_loss = 1 - (2 * (probability * target).sum() + 1) / (probability.sum() + (target * valid).sum() + 1)
                loss = bce + dice_loss
                if not torch.isfinite(loss):
                    raise ValueError("Training loss became nonfinite.")
                loss.backward()
                optimizer.step()
                total_loss += loss.item()
                batches += 1
                step += 1
            epoch += 1
            checkpoint()
            validation = evaluate(model, dataset.splits["validation"], args, device, control)
            score = validation["dice"]
            if score is not None and score > best_dice:
                best_dice = score
                best_state = {key: value.detach().cpu().clone() for key, value in model.state_dict().items()}
            history.append({"epoch": epoch, "meanTrainingLoss": total_loss / batches, "validation": validation})
            print(json.dumps(history[-1], allow_nan=False), flush=True)
            checkpoint()
            write_json(output / "training-report.json", report)
        if best_state is not None:
            model.load_state_dict(best_state)
        report["test"] = evaluate(model, dataset.splits["test"], args, device, control)
        report["export"] = export_bundle(model, args, output, dataset, control)
        report["status"] = "completed"
        write_json(output / "training-report.json", report)
        return 0
    except TrainingStopped as error:
        checkpoint()
        report.update(status="paused", reason=str(error), completedEpochs=epoch, steps=step)
        write_json(output / "training-report.json", report)
        print(str(error), file=sys.stderr)
        return 75
    except Exception as error:
        checkpoint()
        report.update(status="failed", reason=str(error), completedEpochs=epoch, steps=step)
        write_json(output / "training-report.json", report)
        raise


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("validate-dataset", "train"))
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--model-id", default="jp-defect")
    parser.add_argument("--model-version", default="1")
    parser.add_argument("--epochs", type=int, default=30)
    parser.add_argument("--tile-size", type=int, default=512)
    parser.add_argument("--overlap", type=int, default=64)
    parser.add_argument("--batch-size", type=int, default=4)
    parser.add_argument("--learning-rate", type=float, default=1e-3)
    parser.add_argument("--positive-weight", type=float, default=1.0)
    parser.add_argument("--threshold", type=float, default=.5)
    parser.add_argument("--max-minutes", type=float, default=60)
    parser.add_argument("--device", choices=("cuda", "cpu"), default="cuda")
    parser.add_argument("--seed", type=int, default=17)
    parser.add_argument("--resume", type=Path)
    args = parser.parse_args()
    if not 32 <= args.tile_size <= 2048 or args.tile_size % 4 or not 0 <= args.overlap < args.tile_size:
        parser.error("tile-size must be 32..2048 and divisible by 4; overlap must be 0..tile-size-1.")
    if not 1 <= args.epochs <= 10000 or not 1 <= args.batch_size <= 128 or not 0 <= args.threshold <= 1:
        parser.error("Invalid epochs, batch-size or threshold.")
    if not all(math.isfinite(x) and x > 0 for x in (args.learning_rate, args.positive_weight, args.max_minutes)):
        parser.error("learning-rate, positive-weight and max-minutes must be finite and positive.")
    if not args.model_id.strip() or not args.model_version.strip() or len(args.model_id) > 256 or len(args.model_version) > 256:
        parser.error("Model ID/version must be nonempty and at most 256 characters.")
    if args.command == "train" and args.output is None:
        parser.error("train requires --output to an empty directory.")
    dataset = validate_dataset(args.manifest, args.root)
    if args.command == "validate-dataset":
        print(json.dumps(dataset.audit, ensure_ascii=False, indent=2))
        return 0
    return run_training(args, dataset)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (ValueError, OSError) as error:
        print(f"JP-Vision training: {error}", file=sys.stderr)
        raise SystemExit(2)
