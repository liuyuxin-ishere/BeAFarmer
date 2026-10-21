using System.Collections.Generic;
using Game.Framework.Platform;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.TestTools;

namespace Game.Framework.PlayModeTests
{
    public sealed class MouseKeyboardInputTests
    {
        readonly List<InputDevice> _removed = new List<InputDevice>();
        InputActionAsset _asset;
        MouseKeyboardInput _input;
        Keyboard _keyboard;
        Mouse _mouse;
        InputSettings.UpdateMode _previousUpdateMode;
        InputSettings.BackgroundBehavior _previousBackgroundBehavior;
#if UNITY_EDITOR
        InputSettings.EditorInputBehaviorInPlayMode _previousEditorBehavior;
#endif

        [SetUp]
        public void SetUp()
        {
            // Explicitly own input updates and focus routing in headless TestRunner too.
            _previousUpdateMode = InputSystem.settings.updateMode;
            _previousBackgroundBehavior = InputSystem.settings.backgroundBehavior;
            InputSystem.settings.updateMode = InputSettings.UpdateMode.ProcessEventsManually;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
#if UNITY_EDITOR
            _previousEditorBehavior = InputSystem.settings.editorInputBehaviorInPlayMode;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
#endif
            var present = new List<InputDevice>();
            foreach (var device in InputSystem.devices)
                present.Add(device);
            for (int i = 0; i < present.Count; i++)
            {
                if (present[i] is Keyboard || present[i] is Mouse)
                {
                    InputSystem.RemoveDevice(present[i]);
                    _removed.Add(present[i]);
                }
            }

            _keyboard = InputSystem.AddDevice<Keyboard>();
            _mouse = InputSystem.AddDevice<Mouse>();
            _asset = CreateAsset();
            _input = new MouseKeyboardInput(_asset);
            _input.Enable();
        }

        [TearDown]
        public void TearDown()
        {
            if (_input != null)
                _input.Disable();
            if (_keyboard != null)
                InputSystem.RemoveDevice(_keyboard);
            if (_mouse != null)
                InputSystem.RemoveDevice(_mouse);
            for (int i = 0; i < _removed.Count; i++)
                InputSystem.AddDevice(_removed[i]);
            _removed.Clear();
            if (_asset != null)
                Object.Destroy(_asset);
            InputSystem.settings.updateMode = _previousUpdateMode;
            InputSystem.settings.backgroundBehavior = _previousBackgroundBehavior;
#if UNITY_EDITOR
            InputSystem.settings.editorInputBehaviorInPlayMode = _previousEditorBehavior;
#endif
        }

        [Test]
        public void Move_LogsNormalizedVector()
        {
            LogAssert.Expect(LogType.Log, "[Input] move=(0,1)");
            SetKeyboard(Key.W);
            _input.Tick();

            Vector2 move = _input.ReadMove();
            Assert.That(move.y, Is.GreaterThan(0f));
            Assert.That(move.magnitude, Is.LessThanOrEqualTo(1f));
        }

        [Test]
        public void Tap_LogsWithoutDrag()
        {
            int drags = 0;
            _input.DragStarted += _ => drags++;
            _input.Dragged += _ => drags++;
            _input.DragEnded += _ => drags++;

            SetMouse(new Vector2(5f, 5f), true, 0f);
            _input.Tick();
            Assert.That(_input.WasTapPressed(), Is.False);

            LogAssert.Expect(LogType.Log, "[Input] tap=(6,6)");
            SetMouse(new Vector2(6f, 6f), false, 0f);
            _input.Tick();

            Assert.That(_input.WasTapPressed(), Is.True);
            Assert.That(drags, Is.EqualTo(0));
        }

        [Test]
        public void Drag_LogsThreePhases()
        {
            var phases = new List<string>();
            _input.DragStarted += _ => phases.Add("start");
            _input.Dragged += _ => phases.Add("drag");
            _input.DragEnded += _ => phases.Add("end");

            SetMouse(new Vector2(0f, 0f), true, 0f);
            _input.Tick();
            Assert.That(phases, Is.Empty);
            Assert.That(_input.WasTapPressed(), Is.False);

            LogAssert.Expect(LogType.Log, "[Input] drag-start=(0,0)");
            LogAssert.Expect(LogType.Log, "[Input] drag=(20,0)");
            SetMouse(new Vector2(20f, 0f), true, 0f);
            _input.Tick();
            Assert.That(_input.WasTapPressed(), Is.False);

            LogAssert.Expect(LogType.Log, "[Input] drag-end=(20,0)");
            SetMouse(new Vector2(20f, 0f), false, 0f);
            _input.Tick();

            Assert.That(phases, Is.EqualTo(new[] { "start", "drag", "end" }));
            Assert.That(_input.WasTapPressed(), Is.False);
        }

        [Test]
        public void Zoom_LogsScroll()
        {
            LogAssert.Expect(LogType.Log, "[Input] zoom=2.5");
            SetMouse(new Vector2(0f, 0f), false, 2.5f);
            _input.Tick();
            Assert.That(_input.ReadZoom(), Is.EqualTo(2.5f));
        }

        [Test]
        public void Disable_SuppressesOutput()
        {
            var lines = new List<string>();
            Application.LogCallback handler = (condition, stack, type) =>
            {
                if (condition.StartsWith("[Input]"))
                    lines.Add(condition);
            };
            Application.logMessageReceived += handler;
            try
            {
                _input.Disable();
                int drags = 0;
                _input.DragStarted += _ => drags++;
                SetKeyboard(Key.W);
                _input.Tick();
                Assert.That(lines, Is.Empty);
                Assert.That(_input.ReadMove(), Is.EqualTo(Vector2.zero));
                Assert.That(drags, Is.EqualTo(0));
            }
            finally
            {
                Application.logMessageReceived -= handler;
            }
        }

        static InputActionAsset CreateAsset()
        {
            var asset = ScriptableObject.CreateInstance<InputActionAsset>();
            InputActionMap map = asset.AddActionMap("MouseKeyboard");
            map.AddAction("Move", InputActionType.Value)
                .AddCompositeBinding("Dpad")
                .With("Up", "<Keyboard>/w")
                .With("Down", "<Keyboard>/s")
                .With("Left", "<Keyboard>/a")
                .With("Right", "<Keyboard>/d");
            map.AddAction("Point", InputActionType.Value)
                .AddBinding("<Mouse>/position");
            map.AddAction("Press", InputActionType.Button)
                .AddBinding("<Mouse>/leftButton");
            map.AddAction("Zoom", InputActionType.Value)
                .AddBinding("<Mouse>/scroll/y");
            return asset;
        }

        void SetKeyboard(Key key)
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(key));
            InputSystem.Update();
        }

        void SetMouse(Vector2 position, bool leftDown, float scrollY)
        {
            MouseState state = new MouseState
            {
                position = position,
                scroll = new Vector2(0f, scrollY)
            };
            if (leftDown)
                state = state.WithButton(MouseButton.Left);
            InputSystem.QueueStateEvent(_mouse, state);
            InputSystem.Update();
        }
    }
}
