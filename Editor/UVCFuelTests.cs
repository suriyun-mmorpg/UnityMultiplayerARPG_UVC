using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MultiplayerARPG.Tests
{
    public class UVCFuelTests
    {
        private class Controls : PG.ICarControl
        {
            public float Acceleration => 0f;
            public float BrakeReverse => 1f;
            public float Horizontal => 0f;
            public float Pitch => 0f;
            public bool HandBrake { get; set; }
            public bool Boost => false;
        }

        private GameObject _root;
        private PG.CarController _car;
        private PG.Wheel _wheel;
        private Controls _controls;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("UVC fuel cutoff test");
            _root.SetActive(false);
            _car = _root.AddComponent<PG.CarController>();
            var wheel = new GameObject("Wheel");
            wheel.transform.SetParent(_root.transform, false);
            _wheel = wheel.AddComponent<PG.Wheel>();
            _wheel.DriveWheel = true;
            _wheel.HandBrakeWheel = true;
            _wheel.MaxBrakeTorque = 1000f;
            VehicleFuelTests.SetProperty(_wheel, "WheelCollider", wheel.GetComponent<WheelCollider>());
            _car.Wheels = new[] { _wheel };
            _car.Steer = new PG.CarController.SteerConfig();
            _controls = new Controls();
            _car.CarControl = _controls;
            _root.SetActive(true); // PhysX ignores torque writes on inactive colliders.
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_root);

        [Test]
        public void EmptyTankCutsTorqueAndEngineWithoutStoppingTheBody()
        {
            var body = _root.GetComponent<Rigidbody>();
            body.velocity = Vector3.forward * 5f;
            VehicleFuelTests.SetProperty(_car, "EngineIsOn", true);
            VehicleFuelTests.SetProperty(_car, "EngineRPM", 2000f);
            _wheel.WheelCollider.motorTorque = 400f;
            Assert.That(_wheel.WheelCollider.motorTorque, Is.EqualTo(400f));
            _car.ApplyUVCFuelCutoff(0f, 0.02f);
            Assert.That(_car.EngineIsOn, Is.False);
            Assert.That(_car.EngineRPM, Is.Zero);
            Assert.That(_wheel.WheelCollider.motorTorque, Is.Zero);
            Assert.That(body.isKinematic, Is.False);
            Assert.That(body.velocity, Is.EqualTo(Vector3.forward * 5f));
            Assert.That(_car.enabled, Is.True);
        }

        [Test]
        public void BrakeRampSurvivesUvcsEngineOffBrakeResetAndCanRelease()
        {
            _car.ApplyUVCFuelCutoff(1f, 0.02f);
            float first = _wheel.WheelCollider.brakeTorque;
            Assert.That(first, Is.GreaterThan(0f));
            _wheel.WheelCollider.brakeTorque = 0f; // UVC engine-off wheel update.
            _car.ApplyUVCFuelCutoff(1f, 0.02f);
            Assert.That(_wheel.WheelCollider.brakeTorque, Is.GreaterThan(first));
            _car.ApplyUVCFuelCutoff(0f, 0.02f);
            Assert.That(_wheel.WheelCollider.brakeTorque, Is.Zero);
        }

        [Test]
        public void HandbrakeAndAbsContinueWorkingWithoutFuel()
        {
            _controls.HandBrake = true;
            _car.ApplyUVCFuelCutoff(0f, 0.02f);
            Assert.That(_wheel.WheelCollider.brakeTorque, Is.GreaterThan(0f));
            _controls.HandBrake = false;
            _car.Steer.ABS = 1f;
            VehicleFuelTests.SetProperty(_wheel, "ForwardSlipNormalized", 4f);
            _car.ApplyUVCFuelCutoff(1f, 0.02f);
            Assert.That(_car.ABSIsActive, Is.True);
            Assert.That(_wheel.WheelCollider.brakeTorque, Is.Zero);
        }

        [Test]
        public void ReverseBrakingUsesTheCorrectPedalAndBoostNeedsFuel()
        {
            var movement = _root.AddComponent<UVCVehicleEntityMovement>();
            VehicleFuelTests.SetProperty(movement, "Car", _car);
            _car.Gearbox = new PG.CarController.GearboxConfig { AutomaticGearBox = true };
            typeof(UVCVehicleEntityMovement).GetField("_simulationInput", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(movement, new UVCVehicleInput { throttle = 0.7f, brakeReverse = 0.2f, boost = true });
            VehicleFuelTests.SetProperty(_car, "CurrentGear", -1);
            Assert.That(movement.ServiceBrake, Is.EqualTo(0.7f));
            VehicleFuelTests.SetProperty(_car, "CurrentGear", 1);
            Assert.That(movement.ServiceBrake, Is.EqualTo(0.2f));
            Assert.That(movement.Acceleration, Is.EqualTo(0.7f)); // Optional tank absent.
            var vehicle = _root.AddComponent<VehicleEntity>();
            var fuel = _root.AddComponent<VehicleFuelComponent>();
            fuel.OnIdentityInitialize(); // Client defaults to an empty replicated tank.
            VehicleFuelTests.SetProperty(movement, "Fuel", fuel);
            Assert.That(movement.Acceleration, Is.Zero);
            Assert.That(movement.Boost, Is.False);
            Assert.That(movement.BrakeReverse, Is.EqualTo(0.2f));
        }
    }
}
