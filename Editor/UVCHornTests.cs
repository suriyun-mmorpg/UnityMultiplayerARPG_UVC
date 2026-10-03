using System.Reflection;
using LiteNetLib.Utils;
using NUnit.Framework;
using UnityEngine;

namespace MultiplayerARPG.Tests
{
    public class UVCHornTests
    {
        [TestCase(false, false, false)]
        [TestCase(true, false, true)]
        [TestCase(false, true, true)]
        [TestCase(true, true, true)]
        public void HornUsesASpareFlagWithoutChangingInputPacketSize(bool brake, bool boost, bool horn)
        {
            var input = new UVCVehicleInput { steering = -.25f, throttle = .75f, brakeReverse = .4f, pitch = -.5f, handbrake = brake, boost = boost, horn = horn };
            var writer = new NetDataWriter();
            input.Write(writer);
            Assert.That(writer.Length, Is.EqualTo(17));
            var reader = new NetDataReader(writer.CopyData());
            var received = UVCVehicleInput.Read(reader);
            Assert.That(reader.AvailableBytes, Is.Zero);
            Assert.That(received.horn, Is.EqualTo(horn));
            Assert.That(received.boost, Is.EqualTo(boost));
            Assert.That(received.handbrake, Is.EqualTo(brake));
            Assert.That(received.steering, Is.EqualTo(input.steering));
            Assert.That(received.throttle, Is.EqualTo(input.throttle));
            Assert.That(received.brakeReverse, Is.EqualTo(input.brakeReverse));
            Assert.That(received.pitch, Is.EqualTo(input.pitch));
        }

        [Test]
        public void ReleaseAndTimeoutCannotBeUndoneByDelayedHornInput()
        {
            var session = new UVCVehicleControlSession();
            session.UpdateDriver(10, 1);
            var pressed = new UVCVehicleInput { horn = true };
            Assert.That(session.Accept(session.Generation, 10, pressed, 1f), Is.True);
            Assert.That(session.GetInput(1.1f, .5f, true).horn, Is.True);
            Assert.That(session.Accept(session.Generation, 11, default, 1.2f), Is.True);
            Assert.That(session.Accept(session.Generation, 10, pressed, 1.3f), Is.False);
            Assert.That(session.GetInput(1.3f, .5f, true).horn, Is.False);
            session.Accept(session.Generation, 12, pressed, 2f);
            Assert.That(session.GetInput(2.6f, .5f, true).horn, Is.False);
            Assert.That(session.GetInput(2.1f, .5f, false).horn, Is.False);
        }

        [Test]
        public void PreviousDriversHornDoesNotSurviveSeatOrOwnerChanges()
        {
            var session = new UVCVehicleControlSession();
            session.UpdateDriver(10, 1);
            uint oldGeneration = session.Generation;
            session.Accept(oldGeneration, 10, new UVCVehicleInput { horn = true }, 1f);
            session.UpdateDriver(20, 2);
            Assert.That(session.GetInput(1.1f, .5f, true).horn, Is.False);
            Assert.That(session.Accept(oldGeneration, 11, new UVCVehicleInput { horn = true }, 1.1f), Is.False);
            session.UpdateDriver(-1, 0);
            Assert.That(session.GetInput(1.1f, .5f, true).horn, Is.False);
        }

        [Test]
        public void HornInputDoesNotReplacePedalsAndFocusLossReleasesIt()
        {
            var root = new GameObject("Horn controller");
            root.SetActive(false);
            try
            {
                var controller = root.AddComponent<UVCVehiclePlayerController>();
                var manager = root.AddComponent<HornTestManager>();
                VehicleFuelTests.SetProperty(manager, "IsServer", true);
                VehicleFuelTests.SetProperty(manager, "IsClient", true);
                VehicleFuelTests.SetProperty(manager, "ClientConnectionId", 10L);
                var vehicle = root.AddComponent<VehicleEntity>();
                var playerObject = new GameObject("Driver");
                playerObject.transform.SetParent(root.transform, false);
                var driver = playerObject.AddComponent<PlayerCharacterEntity>();
                foreach (var entity in new BaseGameEntity[] { vehicle, driver })
                {
                    VehicleFuelTests.SetProperty(entity.Identity, "Manager", manager);
                    VehicleFuelTests.SetProperty(entity.Identity, "ConnectionId", 10L);
                }
                vehicle.CurrentHp = driver.CurrentHp = 100;
                vehicle.Seats.Add(new VehicleSeat());
                var passengers = (System.Collections.Generic.Dictionary<byte, BaseGameEntity>)typeof(VehicleEntity)
                    .GetField("_passengers", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(vehicle);
                passengers[0] = driver;
                var horn = root.AddComponent<VehicleHornComponent>();
                horn.OnIdentityInitialize();
                var movement = root.AddComponent<UVCVehicleEntityMovement>();
                VehicleFuelTests.SetProperty(movement, "Horn", horn);
                VehicleFuelTests.SetProperty(controller, "Vehicle", vehicle);
                VehicleFuelTests.SetProperty(controller, "SeatIndex", (byte)0);
                typeof(UVCVehiclePlayerController).GetField("_movement", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(controller, movement);
                Assert.That(controller.CanDrive, Is.True);
                controller.UseBoundInputs();
                controller.SetHorn(true);
                Assert.That(horn.LocalInput, Is.True);
                Assert.That(typeof(UVCVehiclePlayerController).GetField("_useMobileInput", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller), Is.False);
                controller.SetThrottle(.6f);
                controller.SetHorn(false);
                var pedals = (UVCVehicleInput)typeof(UVCVehiclePlayerController).GetField("_mobileInput", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(controller);
                Assert.That(pedals.throttle, Is.EqualTo(.6f));
                controller.SetHorn(true);
                horn.SetLocalPresentation(true);
                VehicleFuelTests.SetProperty(controller, "PlayerController", root.AddComponent<VehicleControllerTestOwner>());
                typeof(BaseVehiclePlayerController).GetMethod("OnApplicationFocus", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(controller, new object[] { false });
                Assert.That(horn.LocalInput || horn.ShouldPlayAudio, Is.False);
            }
            finally { Object.DestroyImmediate(root); }
        }
    }
}
