﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace Foster.Framework
{
    public class InputState
    {
        public const int MaxControllers = 8;

        public readonly Keyboard Keyboard;
        public readonly Mouse Mouse;

        private readonly Controller[] controllers;
        public readonly ReadOnlyCollection<Controller> Controllers;

        public InputState()
        {
            controllers = new Controller[MaxControllers];
            for (int i = 0; i < controllers.Length; i++)
                controllers[i] = new Controller();

            Controllers = new ReadOnlyCollection<Controller>(controllers);
            Keyboard = new Keyboard();
            Mouse = new Mouse();
        }

        internal void Step()
        {
            for (int i = 0; i < Controllers.Count; i++)
            {
                if (Controllers[i].Connected)
                    Controllers[i].Step();
            }
            Keyboard.Step();
            Mouse.Step();
        }

        internal void Copy(InputState other)
        {
            for (int i = 0; i < Controllers.Count; i++)
            {
                if (other.Controllers[i].Connected || (Controllers[i].Connected != other.Controllers[i].Connected))
                    Controllers[i].Copy(other.Controllers[i]);
            }

            Keyboard.Copy(other.Keyboard);
            Mouse.Copy(other.Mouse);
        }
    }
}
