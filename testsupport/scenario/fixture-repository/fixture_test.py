from pathlib import Path
import unittest


class FixtureTest(unittest.TestCase):
    def test_result_is_passing(self) -> None:
        self.assertEqual("pass", Path(__file__).with_name("result.txt").read_text().strip())


if __name__ == "__main__":
    unittest.main()
