import copy
import json
from pathlib import Path
import tempfile
import unittest

from PIL import Image

from dataset import metrics, safe_file, tile_starts, validate_dataset
from train import StopControl, TrainingStopped


class DatasetTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.root = Path(self.temporary.name)
        self.manifest = self.root / "dataset.json"
        self.definition = {"schemaVersion": 1, "datasetId": "part-A", "version": "1", "sourceImages": "raw"}
        for index, split in enumerate(("train", "validation", "test")):
            Image.new("RGB", (64, 32), (index * 30, 50, 60)).save(self.root / (split + ".png"))
            mask = Image.new("L", (64, 32), 0)
            mask.putpixel((20, 10), 255)
            mask.save(self.root / (split + "-mask.png"))
            self.definition[split] = [{"image": split + ".png", "mask": split + "-mask.png", "group": split + "-part"}]
        self.write()

    def tearDown(self):
        self.temporary.cleanup()

    def write(self):
        self.manifest.write_text(json.dumps(self.definition), encoding="utf-8")

    def validate(self):
        self.write()
        return validate_dataset(self.manifest, self.root)

    def test_valid_dataset_has_stable_content_identity(self):
        first = self.validate()
        self.assertEqual(1, first.splits["train"][0].foreground_pixels)
        self.assertEqual(1, first.audit["splits"]["train"]["defectImages"])
        self.manifest.write_text(json.dumps(self.definition, indent=4), encoding="utf-8")
        self.assertEqual(first.sha256, validate_dataset(self.manifest, self.root).sha256)
        self.assertEqual(64, len(first.sha256))

    def test_image_changes_change_dataset_identity(self):
        first = self.validate().sha256
        Image.new("RGB", (64, 32), (80, 50, 60)).save(self.root / "train.png")
        self.assertNotEqual(first, self.validate().sha256)

    def test_rejects_group_leakage(self):
        self.definition["test"][0]["group"] = "train-part"
        with self.assertRaisesRegex(ValueError, "Group leakage"):
            self.validate()

    def test_rejects_identical_image_bytes_across_different_groups(self):
        (self.root / "test.png").write_bytes((self.root / "train.png").read_bytes())
        with self.assertRaisesRegex(ValueError, "Identical image"):
            self.validate()

    def test_rejects_parent_absolute_and_windows_path_forms(self):
        for path in ("../secret.png", "/tmp/secret.png", "C:/secret.png", "dir\\secret.png", "./train.png"):
            with self.subTest(path=path), self.assertRaises(ValueError):
                safe_file(self.root.resolve(), path)

    def test_rejects_duplicate_json_keys(self):
        self.manifest.write_text('{"schemaVersion":1,"schemaVersion":1}', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            validate_dataset(self.manifest, self.root)

    def test_rejects_nonfinite_json(self):
        self.manifest.write_text('{"schemaVersion":NaN}', encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "Nonfinite"):
            validate_dataset(self.manifest, self.root)

    def test_masks_require_binary_gray8(self):
        Image.new("L", (64, 32), 128).save(self.root / "train-mask.png")
        with self.assertRaisesRegex(ValueError, "only 0"):
            self.validate()
        Image.new("RGB", (64, 32), (0, 0, 0)).save(self.root / "train-mask.png")
        with self.assertRaisesRegex(ValueError, "Gray8"):
            self.validate()

    def test_masks_require_matching_shape(self):
        Image.new("L", (32, 32), 255).save(self.root / "train-mask.png")
        with self.assertRaisesRegex(ValueError, "match"):
            self.validate()

    def test_long_narrow_production_dimensions_are_accepted(self):
        Image.new("RGB", (10918, 287), (80, 50, 60)).save(self.root / "train.png")
        mask = Image.new("L", (10918, 287), 0)
        mask.putpixel((10000, 140), 255)
        mask.save(self.root / "train-mask.png")
        sample = self.validate().splits["train"][0]
        self.assertEqual((10918, 287), (sample.width, sample.height))
        self.assertEqual(1, sample.foreground_pixels)

    def test_native_axis_budget_is_enforced_before_training(self):
        Image.new("RGB", (32769, 1), (80, 50, 60)).save(self.root / "train.png")
        Image.new("L", (32769, 1), 255).save(self.root / "train-mask.png")
        with self.assertRaisesRegex(ValueError, "native dimension"):
            self.validate()

    def test_symlinks_inside_dataset_are_rejected(self):
        alias = self.root / "alias.png"
        try:
            alias.symlink_to(self.root / "train.png")
        except OSError:
            self.skipTest("Windows symlink creation is unavailable in this process.")
        with self.assertRaisesRegex(ValueError, "Symlink"):
            safe_file(self.root.resolve(), "alias.png")

    def test_all_good_training_requires_anomaly_route(self):
        Image.new("L", (64, 32), 0).save(self.root / "train-mask.png")
        with self.assertRaisesRegex(ValueError, "anomaly"):
            self.validate()

    def test_requires_explicit_raw_original_declaration(self):
        self.definition["sourceImages"] = "annotated"
        with self.assertRaisesRegex(ValueError, "clean original"):
            self.validate()

    def test_each_split_requires_independent_images(self):
        self.definition["validation"] = []
        with self.assertRaisesRegex(ValueError, "at least one"):
            self.validate()

    def test_same_image_cannot_be_duplicated_in_training(self):
        self.definition["train"].append(copy.deepcopy(self.definition["train"][0]))
        with self.assertRaisesRegex(ValueError, "duplicated"):
            self.validate()

    def test_tile_starts_preserve_long_narrow_image_extent(self):
        xs = tile_starts(10918, 512, 64)
        self.assertEqual(0, xs[0])
        self.assertEqual(10918 - 512, xs[-1])
        covered = bytearray(10918)
        for start in xs:
            covered[start:start + 512] = b"\1" * 512
        self.assertTrue(all(covered))
        self.assertEqual((0,), tile_starts(287, 512, 64))

    def test_invalid_overlap_is_rejected(self):
        for overlap in (-1, 512, 513):
            with self.subTest(overlap=overlap), self.assertRaises(ValueError):
                tile_starts(1000, 512, overlap)

    def test_metrics_do_not_invent_accuracy_for_empty_sets(self):
        self.assertIsNone(metrics(0, 0, 0)["dice"])
        self.assertIsNone(metrics(0, 0, 0)["recall"])
        self.assertEqual(.5, metrics(5, 5, 5)["dice"])

    def test_stop_control_explicit_request_stops_at_checkpoint(self):
        control = StopControl(1)
        control.check()
        control.request()
        with self.assertRaises(TrainingStopped):
            control.check()


if __name__ == "__main__":
    unittest.main()
