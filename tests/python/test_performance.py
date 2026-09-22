import copy
import math
import unittest

from ksp_autocraft.performance import assess_performance, aircraft_lifting_part, landing_gear_part, engine_output, targets_for, vehicle_type, _inside_support_polygon
from ksp_autocraft.physics import STANDARD_GRAVITY as G
from ksp_autocraft.designer import select_parts


def v(x=0,y=0,z=0): return {"x":x,"y":y,"z":z}
def constant(value): return [{"x":0,"y":value,"inTangent":0,"outTangent":0}]
ENV = {"pressureKpa":101.325,"densityKgPerCubicMetre":1.225,"speedOfSound":340,"gravity":G,"oxygen":True}
WORLD = {"homeBody":"Home","aerodynamicModel":"Stock","liftMultiplier":.036,"liftDragMultiplier":.015,
         "bodies":[{"name":"Home","radiusMetres":600000,"gravitationalParameter":3530394000000,"atmosphere":True,"atmosphereDepthMetres":70000}]}


def resource(name="Fuel", amount=10, density=.1):
    return {"name":name,"amount":amount,"maxAmount":amount,"densityTonnesPerUnit":density,"unitCost":1}


def engine(thrust=100, isp=300, direction=None, family="rocket", mixture=None):
    return {"supported":True,"defaultMode":True,"family":family,"nominalMaxThrustKn":thrust,"vacuumIspSeconds":isp,
            "massFlowTonnesPerSecond":thrust/(G*isp),"thrustLimiter":1,"ispCurve":constant(isp),
            "thrustDirection":direction or v(0,1,0),"thrustPosition":v(),
            "mixture":mixture or [{"name":"Fuel","ratio":1,"density":.1,"flowMode":"STACK_PRIORITY_SEARCH","ignoreForIsp":False}],
            "propellants":["Fuel"],"flowCap":1e20,"flowCapSharpness":2}


def definition(name, mass=1, resources=None, engines=None, **extras):
    return {"name":name,"dryMassTonnes":mass,"resources":resources or [],"engines":engines or [],"aero":[],"intakes":[],
            "moduleNames":[],"fuelCrossFeed":True,"separators":[],"prefabSize":v(1,1,1),"massOffset":v(),"liftOffset":v(),**extras}


def fixture(definitions, stages=None, positions=None, parents=None):
    stages=stages or [0]*len(definitions);positions=positions or [v() for _ in definitions]
    plan={"facility":"VAB","parts":[]}
    for i,d in enumerate(definitions):
        item={"id":str(i),"partName":d["name"],"stage":stages[i]}
        if i: item.update(parentId=str(parents[i] if parents else i-1),parentNodeId="bottom",childNodeId="top")
        plan["parts"].append(item)
    validation={"placements":[{"id":str(i),"position":p,"rotation":{"x":0,"y":0,"z":0,"w":1}} for i,p in enumerate(positions)]}
    return plan,definitions,validation


def rocket_targets(**extra):
    return {"vehicle":"rocket","mission":"orbit","fuelReserveFraction":.05,"reservedTestParts":[],"requiredDeltaV":100,
            "minLaunchTwr":1.2,"maxThrustOffsetMetres":.25,**extra}


class RocketTests(unittest.TestCase):
    def assess(self,data,targets=None): return assess_performance(*data,targets or rocket_targets(),WORLD,ENV)
    def test_single_stage_rocket_equation_with_reserve_and_no_input_mutation(self):
        d=fixture([definition("rocket",resources=[resource()],engines=[engine()],moduleNames=["ModuleCommand"])])
        original=copy.deepcopy(d)
        result=self.assess(d)
        self.assertEqual(result["status"],"passed",result)
        self.assertAlmostEqual(result["metrics"]["deltaVVacuumWithReserve"],300*G*math.log(2/1.05),places=6)
        self.assertAlmostEqual(result["metrics"]["launchTwr"],100/(2*G),places=6)
        self.assertEqual(d,original)

    def test_two_stages_include_payload_and_jettison_only_at_the_correct_event(self):
        definitions=[definition("pod",moduleNames=["ModuleCommand"]),definition("upperTank",.1,[resource()]),
                     definition("upperEngine",.2,engines=[engine(50,320)]),
                     definition("decoupler",.05,fuelCrossFeed=False,separators=[{"nodeId":"bottom","omni":False}]),
                     definition("lowerTank",.2,[resource(amount=20)]),definition("lowerEngine",.5,engines=[engine(150,250)])]
        result=self.assess(fixture(definitions,[-1,-1,0,1,-1,2]))
        expected=250*G*math.log(5.05/3.15)+320*G*math.log(2.35/1.4)
        self.assertAlmostEqual(result["metrics"]["deltaVVacuumWithReserve"],expected,places=5)
        self.assertEqual([s["stage"] for s in result["metrics"]["stages"]],[2,0])

    def test_mixture_is_limited_by_the_first_exhausted_resource(self):
        mix=[{"name":n,"ratio":1,"density":.1,"flowMode":"NO_FLOW","ignoreForIsp":False} for n in ("Fuel","Ox")]
        d=fixture([definition("rocket",resources=[resource(),resource("Ox",5)],engines=[engine(mixture=mix)],moduleNames=["ModuleCommand"])])
        result=self.assess(d)
        self.assertAlmostEqual(result["metrics"]["deltaVVacuumWithReserve"],300*G*math.log(2.5/1.55),places=6)

    def test_no_flow_cannot_use_an_external_tank(self):
        e=engine();e["mixture"][0]["flowMode"]="NO_FLOW"
        result=self.assess(fixture([definition("tank",resources=[resource()],moduleNames=["ModuleCommand"]),definition("engine",engines=[e])]))
        self.assertTrue(result["hasBlockingFailure"])
        self.assertEqual(result["metrics"]["deltaVVacuumWithReserve"],0)

    def test_crossfeed_barrier_prevents_optimistic_delta_v(self):
        d=fixture([definition("tank",resources=[resource()],moduleNames=["ModuleCommand"]),definition("barrier",fuelCrossFeed=False),definition("engine",engines=[engine()])])
        self.assertEqual(self.assess(d)["metrics"]["deltaVVacuumWithReserve"],0)

    def test_low_twr_and_delta_v_are_blocking(self):
        result=self.assess(fixture([definition("heavy",mass=100,resources=[resource()],engines=[engine(1)],moduleNames=["ModuleCommand"])]))
        self.assertEqual(result["status"],"failed")
        self.assertTrue(any("TWR" in issue for issue in result["issues"]))
        self.assertTrue(any("Δv" in issue for issue in result["issues"]))

    def test_opposing_thrust_is_not_added_as_useful_delta_v(self):
        result=self.assess(fixture([definition("rocket",resources=[resource()],engines=[engine(),engine(direction=v(0,-1,0))],moduleNames=["ModuleCommand"])]))
        self.assertEqual(result["metrics"]["deltaVVacuumWithReserve"],0)
        self.assertEqual(result["metrics"]["launchTwr"],0)

    def test_offset_thrust_and_early_chute_are_rejected(self):
        e=engine();e["thrustPosition"]=v(2,0,0)
        result=self.assess(fixture([definition("rocket",resources=[resource()],engines=[e],moduleNames=["ModuleCommand","ModuleParachute"])]))
        self.assertTrue(any("偏置" in issue for issue in result["issues"]))
        self.assertTrue(any("降落伞" in issue for issue in result["issues"]))

    def test_missing_pose_data_is_unverified_not_passed(self):
        d=fixture([definition("rocket",resources=[resource()],engines=[engine()])]);d[2]["placements"]=[]
        self.assertEqual(self.assess(d)["status"],"unverified")


def aircraft_fixture():
    aero={"normal":v(0,1,0),"liftCoefficient":.8,"controlSurface":True,"controlFraction":.5,"controlRange":20,
          "pitch":True,"roll":True,"yaw":False,"perpendicularOnly":True,"internalDrag":True,
          "liftCurve":[{"x":0,"y":0,"inTangent":1,"outTangent":1},{"x":1,"y":1,"inTangent":1,"outTangent":1}],
          "liftMachCurve":constant(1),"dragCurve":constant(.01),"dragMachCurve":constant(1)}
    mix=[{"name":"LiquidFuel","ratio":1,"density":.005,"flowMode":"STACK_PRIORITY_SEARCH","ignoreForIsp":False},
         {"name":"IntakeAir","ratio":10,"density":.005,"flowMode":"ALL_VESSEL","ignoreForIsp":True}]
    jet=engine(100,5000,v(0,0,1),"airbreathing",mix)
    definitions=[definition("cockpit",1,moduleNames=["ModuleCommand"]),definition("tank",.3,[resource("LiquidFuel",450,.005)]),
                 definition("jet",.4,engines=[jet]),
                 definition("left",.05,aero=[copy.deepcopy(aero)],prefabSize=v(4,.1,2)),
                 definition("right",.05,aero=[copy.deepcopy(aero)],prefabSize=v(4,.1,2)),
                 definition("tail",.02,aero=[{**copy.deepcopy(aero),"liftCoefficient":.15}],prefabSize=v(2,.1,1)),
                 definition("fin",.02,aero=[{**copy.deepcopy(aero),"liftCoefficient":.15,"normal":v(1,0,0)}]),
                 definition("noseGear",.03,wheel=True),definition("leftGear",.03,wheel=True),definition("rightGear",.03,wheel=True),
                 definition("intake",.02,intakes=[{"resource":"IntakeAir","area":.1,"intakeSpeed":30,"unitScalar":.4,
                                                   "resourceDensity":.005,"oxygenRequired":True,"direction":v(0,0,1),"machCurve":constant(1)}])]
    positions=[v(0,0,2),v(),v(0,0,-2),v(-2,0,-.2),v(2,0,-.2),v(0,0,-3),v(0,0,-3),v(0,-1,2),v(-1,-1,-.8),v(1,-1,-.8),v(0,0,1)]
    d=fixture(definitions,[-1,-1,0,-1,-1,-1,-1,-1,-1,-1,-1],positions,[None,0,1,1,1,1,1,0,1,1,0]);d[0]["facility"]="SPH"
    return d


class AircraftTests(unittest.TestCase):
    def assess(self,data):
        targets=targets_for("aircraft","plane",WORLD,cruise_speed=120,min_endurance=600)
        return assess_performance(*data,targets,WORLD,ENV,ENV)

    def test_reasonable_aircraft_passes_estimated_screening(self):
        result=self.assess(aircraft_fixture())
        self.assertEqual(result["status"],"passed",result)
        self.assertLessEqual(result["metrics"]["estimatedStallSpeed"],70)
        self.assertFalse(result["trajectorySimulated"])

    def test_vertical_fins_do_not_count_as_main_wings(self):
        d=aircraft_fixture()
        for i in (3,4,5):d[1][i]["aero"][0]["normal"]=v(1,0,0)
        result=self.assess(d)
        self.assertTrue(any("升力不足" in issue for issue in result["issues"]))

    def test_no_intake_cannot_supply_airbreathing_engine(self):
        d=aircraft_fixture();d[1][-1]["intakes"]=[]
        self.assertTrue(any("进气" in issue for issue in self.assess(d)["issues"]))

    def test_one_sided_surfaces_do_not_get_omnidirectional_lift(self):
        d=aircraft_fixture()
        for i in (3,4,5): d[1][i]["aero"][0]["omnidirectional"]=False
        self.assertTrue(any("升力不足" in issue for issue in self.assess(d)["issues"]))

    def test_attached_occlusion_node_disables_lifting_surface(self):
        d=aircraft_fixture()
        for i in (3,4,5):d[1][i]["aero"][0]["disabledByNode"]="top"
        self.assertTrue(any("升力不足" in issue for issue in self.assess(d)["issues"]))

    def test_reverse_engine_and_low_power_are_rejected(self):
        for modification in ("reverse", "low"):
            d=aircraft_fixture()
            if modification=="reverse":d[1][2]["engines"][0]["thrustDirection"]=v(0,0,-1)
            else:d[1][2]["engines"][0]["massFlowTonnesPerSecond"]*=.001
            self.assertTrue(self.assess(d)["hasBlockingFailure"])

    def test_unstable_layout_and_zero_control_authority_fail(self):
        d=aircraft_fixture()
        for i in (3,4,5):d[2]["placements"][i]["position"]["z"]=3;d[1][i]["aero"][0]["controlFraction"]=0
        result=self.assess(d)
        self.assertTrue(any("稳定" in issue for issue in result["issues"]))
        self.assertTrue(any("控制面" in issue for issue in result["issues"]))

    def test_support_polygon_is_not_just_a_bounding_rectangle(self):
        self.assertFalse(_inside_support_polygon([(0,0),(2,0),(0,2)],(1.9,1.9)))
        self.assertTrue(_inside_support_polygon([(-1,-1),(1,-1),(0,2)],(0,0)))
        d=aircraft_fixture()
        for i in (7,8,9):d[2]["placements"][i]["position"]["x"]=0
        self.assertTrue(any("起落架" in issue for issue in self.assess(d)["issues"]))

    def test_native_intake_and_isp_ignore_rules_have_matching_units(self):
        result=self.assess(aircraft_fixture())
        expected=.1*(120+30)*1.225*.4/.005
        self.assertAlmostEqual(result["metrics"]["intakeSupply"]["IntakeAir"],expected)
        mdot=100/(G*5000)
        self.assertAlmostEqual(result["metrics"]["propellantDemandPerSecond"]["LiquidFuel"],mdot/.005)


class IntentAndSelectionTests(unittest.TestCase):
    def test_heatshields_airbrakes_and_legs_are_not_primary_aircraft_equipment(self):
        self.assertFalse(aircraft_lifting_part({"moduleNames":["ModuleAblator"],"aero":[{"liftCoefficient":10}]}))
        self.assertFalse(aircraft_lifting_part({"moduleNames":[],"aero":[{"liftCoefficient":10,"airbrake":True}]}))
        self.assertFalse(landing_gear_part({"name":"miniLandingLeg","wheel":True,"wheelType":"LEG"}))
        self.assertFalse(landing_gear_part({"name":"roverWheel","wheel":True,"wheelType":"MOTORIZED"}))
        self.assertTrue(landing_gear_part({"name":"modularGear","wheel":True,"wheelType":"FREE"}))

    def test_jet_request_does_not_fill_engine_choices_with_propellers(self):
        _, aircraft, _ = aircraft_fixture()
        for p in aircraft: p.update(unlocked=True,surfaceAttach=True,stackAttach=True,defaultCost=100,category="Aero")
        aircraft[2]["category"]="Engine"
        prop=copy.deepcopy(aircraft[2]);prop.update(name="cheapProp",defaultCost=1,roles=["custom_propeller_engine"])
        selected=select_parts(aircraft+[prop],None,40,"aircraft","喷气飞机")
        self.assertIn("jet",[p["name"] for p in selected])
        self.assertNotIn("cheapProp",[p["name"] for p in selected])

    def test_sph_and_aircraft_language_select_aircraft(self):
        self.assertEqual(vehicle_type("设计飞机","VAB"),"aircraft")
        self.assertEqual(vehicle_type("a useful vehicle","SPH"),"aircraft")
        self.assertEqual(vehicle_type("rocket","SPH"),"rocket")

    def test_foreign_mission_needs_explicit_delta_v_and_far_not_silently_stock(self):
        detail={"requirements":[{"targetBody":"Moon","facts":[],"children":[]}]}
        with self.assertRaises(ValueError):targets_for("rocket","moon mission",WORLD,detail)
        self.assertEqual(targets_for("rocket","moon mission",WORLD,detail,required_delta_v=6000)["requiredDeltaV"],6000)
        with self.assertRaises(ValueError):targets_for("aircraft","plane",{**WORLD,"aerodynamicModel":"FAR"})

    def test_aircraft_roles_survive_large_cheap_rocket_catalog(self):
        _, aircraft, _=aircraft_fixture()
        for p in aircraft:p.update(unlocked=True,surfaceAttach=True,stackAttach=True,defaultCost=1000,category="Aero")
        aircraft[0].update(roles=["cockpit"],crewCapacity=1)
        aircraft[1]["roles"]=["liquid_fuel_tank"]
        aircraft[2].update(category="Engine",roles=["airbreathing_engine"])
        rockets=[definition("cheapRocket"+str(i),engines=[engine()],unlocked=True,stackAttach=True,defaultCost=1,category="Engine") for i in range(200)]
        selected=select_parts(rockets+aircraft,None,40,"aircraft")
        names={p["name"] for p in selected}
        self.assertTrue({"cockpit","jet","left","right","intake","tank","noseGear"}.issubset(names))
        self.assertFalse(any(name.startswith("cheapRocket") for name in names))


if __name__=="__main__":unittest.main()
