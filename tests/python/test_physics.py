import math
import unittest

from ksp_autocraft.physics import STANDARD_GRAVITY, delta_v, thrust_to_weight


class PhysicsTests(unittest.TestCase):
    def test_standard_gravity_is_isp_reference(self):
        self.assertEqual(STANDARD_GRAVITY, 9.80665)

    def test_delta_v_rocket_equation(self):
        self.assertAlmostEqual(delta_v(300, 10, 2), 300 * 9.80665 * math.log(5))

    def test_delta_v_depends_on_mass_ratio_not_mass_scale(self):
        self.assertAlmostEqual(delta_v(325, 8, 2), delta_v(325, 0.008, 0.002))

    def test_zero_isp_and_equal_masses(self):
        self.assertEqual(delta_v(0, 10, 2), 0)
        self.assertEqual(delta_v(300, 10, 10), 0)

    def test_delta_v_extreme_mass_ratio(self):
        expected = 9.80665 * (math.log(1e300) - math.log(1e-300))
        self.assertAlmostEqual(delta_v(1, 1e300, 1e-300), expected)

    def test_delta_v_nearly_equal_masses(self):
        wet = math.nextafter(1.0, math.inf)
        self.assertGreater(delta_v(300, wet, 1), 0)
        self.assertAlmostEqual(delta_v(300, wet, 1), 300 * 9.80665 * math.log1p(wet - 1))

    def test_delta_v_rejects_invalid_inputs(self):
        for index in range(3):
            for bad in (math.nan, math.inf, -math.inf, -1, True, "2", None, 10**1000):
                values = [300, 10, 2]
                values[index] = bad
                with self.subTest(index=index, bad=bad), self.assertRaises(ValueError):
                    delta_v(*values)
        for values in ((300, 0, 2), (300, 10, 0), (300, 0, 0), (300, 1, 2)):
            with self.subTest(values=values), self.assertRaises(ValueError):
                delta_v(*values)

    def test_delta_v_rejects_overflow(self):
        with self.assertRaises(ValueError):
            delta_v(1e308, 10, 1)

    def test_twr_kn_and_tonnes_cancel(self):
        self.assertAlmostEqual(thrust_to_weight(100, 5), 100000 / (5000 * 9.80665))

    def test_twr_uses_supplied_local_gravity(self):
        self.assertEqual(thrust_to_weight(120, 5, 3), 8)
        self.assertEqual(thrust_to_weight(120, 5, 6), 4)

    def test_zero_thrust(self):
        self.assertEqual(thrust_to_weight(0, 5, 3), 0)

    def test_twr_rejects_invalid_inputs(self):
        for index in range(3):
            for bad in (math.nan, math.inf, -math.inf, -1, True, "2", None, 10**1000):
                values = [100, 5, 3]
                values[index] = bad
                with self.subTest(index=index, bad=bad), self.assertRaises(ValueError):
                    thrust_to_weight(*values)
        for values in ((100, 0, 3), (100, 5, 0)):
            with self.subTest(values=values), self.assertRaises(ValueError):
                thrust_to_weight(*values)

    def test_twr_rejects_overflow(self):
        with self.assertRaises(ValueError):
            thrust_to_weight(1e308, 1e-308, 1)

    def test_deterministic_results(self):
        self.assertEqual([delta_v(305, 9, 2) for _ in range(10)], [delta_v(305, 9, 2)] * 10)
        self.assertEqual([thrust_to_weight(125, 9, 4) for _ in range(10)], [thrust_to_weight(125, 9, 4)] * 10)


if __name__ == "__main__":
    unittest.main()
