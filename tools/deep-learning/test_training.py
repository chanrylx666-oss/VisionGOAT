"""Optional CPU integration test; skipped unless isolated Torch+ONNX dependencies exist."""
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest

from PIL import Image, ImageDraw

AVAILABLE = all(importlib.util.find_spec(name) is not None for name in ("torch", "onnx", "onnxruntime"))


@unittest.skipUnless(AVAILABLE, "Install training dependencies in an isolated environment for CPU export smoke.")
class TrainingTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.definition = {"schemaVersion": 1, "datasetId": "synthetic-plumbing-only", "version": "1", "sourceImages": "raw"}
        for index, split in enumerate(("train", "validation", "test")):
            image = Image.new("RGB", (43, 35), (30 + index * 10, 40, 50))
            mask = Image.new("L", image.size, 0)
            ImageDraw.Draw(image).rectangle((10, 10, 15, 15), fill=(220, 220, 220))
            ImageDraw.Draw(mask).rectangle((10, 10, 15, 15), fill=255)
            image.save(self.root / (split + ".png"))
            mask.save(self.root / (split + "-mask.png"))
            self.definition[split] = [{"image": split + ".png", "mask": split + "-mask.png", "group": split}]
        (self.root / "dataset.json").write_text(json.dumps(self.definition), encoding="utf-8")

    def tearDown(self):
        self.temporary.cleanup()

    def command(self, output, *extra):
        return [sys.executable, str(Path(__file__).with_name("train.py")), "train",
                "--manifest", str(self.root / "dataset.json"), "--root", str(self.root),
                "--output", str(self.root / output), "--device", "cpu", "--tile-size", "32",
                "--overlap", "8", "--batch-size", "2", "--epochs", "1", *extra]

    def run_job(self, output, expected=0, *extra):
        result = subprocess.run(self.command(output, *extra), text=True, capture_output=True, timeout=120)
        self.assertEqual(expected, result.returncode, result.stdout + result.stderr)
        return self.root / output

    def test_actual_training_exports_checked_native_contract(self):
        output = self.run_job("complete")
        report = json.loads((output / "training-report.json").read_text(encoding="utf-8"))
        self.assertEqual("completed", report["status"])
        self.assertEqual("passed", report["export"]["cpuParity"])
        self.assertEqual(3, report["export"]["probeCount"])
        manifest = json.loads((output / "bundle" / "manifest.json").read_text(encoding="utf-8"))
        self.assertEqual("binarySegmentation", manifest["task"])
        self.assertEqual(["background", "defect"], manifest["labels"])
        self.assertEqual("none", manifest["output"]["activation"])
        self.assertTrue((output / "bundle" / "model.onnx").is_file())

    def test_time_limit_saves_checkpoint_without_success_bundle_and_safe_resume_completes(self):
        paused = self.run_job("paused", 75, "--max-minutes", "0.000001")
        self.assertTrue((paused / "checkpoint.pt").is_file())
        self.assertFalse((paused / "bundle").exists())
        self.assertEqual("paused", json.loads((paused / "training-report.json").read_text(encoding="utf-8"))["status"])
        resumed = self.run_job("resumed", 0, "--resume", str(paused / "checkpoint.pt"))
        self.assertTrue((resumed / "bundle" / "manifest.json").exists())
        # Changing an optimizer/loss/tiling setting cannot silently inherit a different checkpoint setup.
        rejected = subprocess.run(self.command("mismatch", "--resume", str(paused / "checkpoint.pt"),
                                                 "--learning-rate", "0.002"), text=True, capture_output=True, timeout=120)
        self.assertEqual(2, rejected.returncode, rejected.stdout + rejected.stderr)
        self.assertIn("training settings differ", rejected.stderr)
        self.assertFalse((self.root / "mismatch" / "bundle").exists())


if __name__ == "__main__":
    unittest.main()
