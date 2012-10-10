﻿using System;
using System.Collections.Generic;
using Cosmos.IL2CPU.Plugs;
using System.Threading;

namespace Cosmos.Kernel.Plugs {
	[Plug(Target=typeof(System.Threading.Thread))]
	public static class ThreadImpl {
		public static IntPtr InternalGetCurrentThread() {
			return IntPtr.Zero;

		}
        public static void Sleep(int millisecondsTimeout)
        {
            //Cosmos.Hardware.Global.Sleep((uint) millisecondsTimeout);
            Kernel.CPU.Halt();
        }

	}
}