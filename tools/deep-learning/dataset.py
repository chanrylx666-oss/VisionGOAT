"""Validated, group-separated native-resolution image/mask datasets (no Torch needed)."""
from __future__ import annotations

from dataclasses import dataclass
import hashlib
import json
from pathlib import Path, PurePosixPath
import stat
from typing import Any

from PIL import Image

SPLITS = ("train", "validation", "test")
MAX_PIXELS = 32 * 1024 * 1024
Image.MAX_IMAGE_PIXELS = MAX_PIXELS


def unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"Duplicate JSON field: {key}")
        result[key] = value
    return result


def file_sha256(path: Path) -> str:
    result = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            result.update(block)
    return result.hexdigest()


def safe_file(root: Path, name: str) -> Path:
    if not isinstance(name, str) or not name or "\\" in name or ":" in name:
        raise ValueError("Dataset paths must be relative POSIX paths.")
    relative = PurePosixPath(name)
    if relative.is_absolute() or any(p in ("", ".", "..") for p in name.split("/")):
        raise ValueError(f"Dataset path escapes its root: {name}")
    candidate = root
    for part in relative.parts:
        candidate = candidate / part
        metadata = candidate.lstat()
        if stat.S_ISLNK(metadata.st_mode) or getattr(metadata, "st_file_attributes", 0) & 0x400:
            raise ValueError(f"Symlink/reparse paths are not accepted: {name}")
    resolved = candidate.resolve(strict=True)
    if not resolved.is_relative_to(root) or not resolved.is_file():
        raise ValueError(f"Dataset path is not a file inside its root: {name}")
    if not 0 < resolved.stat().st_size <= 512 * 1024 * 1024:
        raise ValueError(f"Dataset file size is outside the 512 MiB limit: {name}")
    return resolved


@dataclass(frozen=True)
class Sample:
    image: Path
    mask: Path
    group: str
    width: int
    height: int
    foreground_pixels: int
    image_hash: str
    mask_hash: str


@dataclass(frozen=True)
class Dataset:
    dataset_id: str
    version: str
    sha256: str
    splits: dict[str, tuple[Sample, ...]]
    audit: dict[str, Any]


def text_field(value: Any, name: str) -> str:
    if not isinstance(value, str) or not value.strip() or len(value) > 256:
        raise ValueError(f"{name} must be a nonempty string of at most 256 characters.")
    return value


def validate_dataset(manifest: Path, root: Path) -> Dataset:
    root = root.resolve(strict=True)
    if not root.is_dir():
        raise ValueError("Dataset root must be a directory.")
    if manifest.stat().st_size > 16 * 1024 * 1024:
        raise ValueError("Dataset manifest exceeds 16 MiB.")
    raw = manifest.read_bytes()
    definition = json.loads(raw.decode("utf-8"), object_pairs_hook=unique_object,
                            parse_constant=lambda x: (_ for _ in ()).throw(ValueError(f"Nonfinite JSON: {x}")))
    if not isinstance(definition, dict) or type(definition.get("schemaVersion")) is not int or definition.get("schemaVersion") != 1:
        raise ValueError("Dataset schemaVersion must be 1.")
    if definition.get("sourceImages") != "raw":
        raise ValueError("Declare sourceImages='raw'; export clean original images, never overlays or reconstructed edges.")
    dataset_id = text_field(definition.get("datasetId"), "datasetId")
    version = text_field(definition.get("version"), "version")
    groups: dict[str, str] = {}
    hashes: dict[str, str] = {}
    paths: set[Path] = set()
    result: dict[str, tuple[Sample, ...]] = {}
    audit_splits: dict[str, Any] = {}
    identity: dict[str, Any] = {"schemaVersion": 1, "datasetId": dataset_id,
                                "version": version, "sourceImages": "raw"}
    total_samples = 0
    for split in SPLITS:
        entries = definition.get(split)
        if not isinstance(entries, list) or not entries:
            raise ValueError(f"{split} must contain at least one image/mask/group entry.")
        total_samples += len(entries)
        if total_samples > 100000:
            raise ValueError("Dataset exceeds 100000 samples.")
        samples: list[Sample] = []
        identities: list[dict[str, Any]] = []
        for entry in entries:
            if not isinstance(entry, dict) or set(entry) != {"image", "mask", "group"}:
                raise ValueError("Each sample must contain exactly image, mask and group.")
            group = text_field(entry["group"], "group")
            if group in groups and groups[group] != split:
                raise ValueError(f"Group leakage between {groups[group]} and {split}: {group}")
            groups[group] = split
            image_path = safe_file(root, entry["image"])
            mask_path = safe_file(root, entry["mask"])
            if image_path == mask_path or image_path in paths:
                raise ValueError("An input image cannot be duplicated or used as its own mask.")
            paths.add(image_path)
            with Image.open(image_path) as image, Image.open(mask_path) as mask:
                width, height = image.size
                if width > 32768 or height > 32768 or width * height > MAX_PIXELS:
                    raise ValueError(f"Image exceeds native dimension/pixel budgets: {entry['image']}")
                if image.mode not in ("L", "RGB") or getattr(image, "n_frames", 1) != 1:
                    raise ValueError("Input images must be single-frame 8-bit Gray/RGB images.")
                if mask.size != image.size or mask.mode != "L" or getattr(mask, "n_frames", 1) != 1:
                    raise ValueError("Masks must be single-frame Gray8 and match image width/height.")
                histogram = mask.histogram()
                if sum(histogram[1:255]):
                    raise ValueError("Masks must contain only 0 (background) and 255 (defect).")
                image.load()  # Validate decoding now rather than discovering corrupt inputs after renting GPU.
                foreground = histogram[255]
            image_hash, mask_hash = file_sha256(image_path), file_sha256(mask_path)
            if image_hash in hashes and hashes[image_hash] != split:
                raise ValueError(f"Identical image bytes leak between {hashes[image_hash]} and {split}.")
            hashes[image_hash] = split
            samples.append(Sample(image_path, mask_path, group, width, height, foreground, image_hash, mask_hash))
            identities.append({**entry, "width": width, "height": height,
                               "imageSha256": image_hash, "maskSha256": mask_hash})
        if split == "train" and not any(x.foreground_pixels for x in samples):
            raise ValueError("Supervised training requires at least one annotated defect; use an anomaly route for all-good data.")
        result[split] = tuple(samples)
        identity[split] = identities
        audit_splits[split] = {"images": len(samples), "groups": len({x.group for x in samples}),
                               "defectImages": sum(x.foreground_pixels > 0 for x in samples),
                               "foregroundPixels": sum(x.foreground_pixels for x in samples)}
    digest = hashlib.sha256(json.dumps(identity, sort_keys=True, separators=(",", ":"),
                                       ensure_ascii=False).encode("utf-8")).hexdigest()
    audit = {"datasetId": dataset_id, "version": version, "datasetSha256": digest,
             "manifestSha256": hashlib.sha256(raw).hexdigest(), "splits": audit_splits,
             "sourceImages": "raw (operator declaration; overlay absence cannot be inferred reliably)",
             "splitPolicy": "group and exact image-content disjoint before tiling"}
    return Dataset(dataset_id, version, digest, result, audit)


def tile_starts(length: int, size: int, overlap: int) -> tuple[int, ...]:
    if length < 1 or size < 1 or not 0 <= overlap < size:
        raise ValueError("Invalid length, tile size or overlap.")
    if length <= size:
        return (0,)
    starts = list(range(0, length - size + 1, size - overlap))
    if starts[-1] != length - size:
        starts.append(length - size)
    return tuple(starts)


def metrics(tp: int, fp: int, fn: int) -> dict[str, float | int | None]:
    """Undefined rates stay null; no invented perfect score for an all-empty test set."""
    return {"truePositivePixels": tp, "falsePositivePixels": fp, "falseNegativePixels": fn,
            "precision": tp / (tp + fp) if tp + fp else None,
            "recall": tp / (tp + fn) if tp + fn else None,
            "dice": 2 * tp / (2 * tp + fp + fn) if 2 * tp + fp + fn else None}
