"""Provider authentication/protocol tests belong to KSPAIHub; these helpers are neutral."""
import unittest

from ksp_autocraft.llm import strict_json


class JsonTests(unittest.TestCase):
    def test_strict_json_rejects_ambiguity_and_nonfinite(self):
        for text in ('{"x":1,"x":2}', '{"x":1e999}', '{"x":Infinity}', '{"x":NaN}'):
            with self.assertRaises(ValueError): strict_json(text)

    def test_unicode_and_nested_values_survive(self):
        self.assertEqual(strict_json('{"任务":{"说明":"设计飞机","数值":[1,2.5]}}'),
                         {"任务": {"说明": "设计飞机", "数值": [1, 2.5]}})


if __name__ == "__main__": unittest.main()
