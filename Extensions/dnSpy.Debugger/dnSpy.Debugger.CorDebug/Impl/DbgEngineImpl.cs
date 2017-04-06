﻿/*
    Copyright (C) 2014-2017 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using dndbg.COM.CorDebug;
using dndbg.DotNet;
using dndbg.Engine;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.CorDebug;
using dnSpy.Contracts.Debugger.DotNet.Metadata;
using dnSpy.Contracts.Debugger.Engine;
using dnSpy.Contracts.Debugger.Exceptions;
using dnSpy.Contracts.Metadata;
using dnSpy.Debugger.CorDebug.DAC;
using dnSpy.Debugger.CorDebug.Properties;

namespace dnSpy.Debugger.CorDebug.Impl {
	abstract partial class DbgEngineImpl : DbgEngine, IClrDacDebugger {
		public override DbgStartKind StartKind { get; }
		public override string[] DebugTags => new[] { PredefinedDebugTags.DotNetDebugger };
		public override event EventHandler<DbgEngineMessage> Message;
		public event EventHandler ClrDacRunning;
		public event EventHandler ClrDacPaused;
		public event EventHandler ClrDacTerminated;

		Dispatcher Dispatcher => debuggerThread.Dispatcher;

		readonly DebuggerThread debuggerThread;
		readonly object lockObj;
		readonly ClrDacProvider clrDacProvider;
		ClrDac clrDac;
		bool clrDacInitd;
		readonly DbgManager dbgManager;
		DnDebugger dnDebugger;
		SafeHandle hProcess_debuggee;
		DbgObjectFactory objectFactory;
		readonly Dictionary<DnAppDomain, DbgEngineAppDomain> toEngineAppDomain;
		readonly Dictionary<DnModule, DbgEngineModule> toEngineModule;
		readonly Dictionary<DnThread, DbgEngineThread> toEngineThread;
		readonly Dictionary<DnAssembly, List<DnModule>> toAssemblyModules;

		protected DbgEngineImpl(ClrDacProvider clrDacProvider, DbgManager dbgManager, DbgStartKind startKind) {
			StartKind = startKind;
			lockObj = new object();
			toEngineAppDomain = new Dictionary<DnAppDomain, DbgEngineAppDomain>();
			toEngineModule = new Dictionary<DnModule, DbgEngineModule>();
			toEngineThread = new Dictionary<DnThread, DbgEngineThread>();
			toAssemblyModules = new Dictionary<DnAssembly, List<DnModule>>();
			this.dbgManager = dbgManager ?? throw new ArgumentNullException(nameof(dbgManager));
			this.clrDacProvider = clrDacProvider ?? throw new ArgumentNullException(nameof(clrDacProvider));
			clrDac = NullClrDac.Instance;
			debuggerThread = new DebuggerThread("CorDebug");
			debuggerThread.CallDispatcherRun();
		}

		internal event EventHandler<ClassLoadedEventArgs> ClassLoaded;

		internal void VerifyCorDebugThread() => Dispatcher.VerifyAccess();
		internal T InvokeCorDebugThread<T>(Func<T> action) => Dispatcher.Invoke(action, DispatcherPriority.Send);
		internal void CorDebugThread(Action action) {
			if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
				Dispatcher.BeginInvoke(DispatcherPriority.Send, action);
		}

		void DnDebugger_DebugCallbackEvent(DnDebugger dbg, DebugCallbackEventArgs e) {
			switch (e.Kind) {
			case DebugCallbackKind.CreateProcess:
				var cp = (CreateProcessDebugCallbackEventArgs)e;
				hProcess_debuggee = Native.NativeMethods.OpenProcess(Native.NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)(cp.CorProcess?.ProcessId ?? -1));
				SendMessage(new DbgMessageConnected(cp.CorProcess.ProcessId, pause: false));
				e.AddPauseReason(DebuggerPauseReason.Other);
				break;

			case DebugCallbackKind.CreateAppDomain:
				// We can't create it in the CreateProcess event
				if (!clrDacInitd) {
					clrDacInitd = true;
					var p = dnDebugger.Processes.FirstOrDefault();
					if (p != null)
						clrDac = clrDacProvider.Create(p.ProcessId, dnDebugger.CLRPath, this);
				}
				break;

			case DebugCallbackKind.Exception2:
				var e2 = (Exception2DebugCallbackEventArgs)e;
				DbgExceptionEventFlags exFlags;
				if (e2.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_FIRST_CHANCE)
					exFlags = DbgExceptionEventFlags.FirstChance;
				else if (e2.EventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED)
					exFlags = DbgExceptionEventFlags.SecondChance | DbgExceptionEventFlags.Unhandled;
				else
					break;
				var exObj = e2.CorThread?.CurrentException;
				objectFactory.CreateException(new DbgExceptionId(PredefinedExceptionCategories.DotNet, TryGetExceptionName(exObj) ?? "???"), exFlags, TryGetExceptionMessage(exObj), TryGetThread(e2.CorThread), TryGetModule(e2.CorFrame, e2.CorThread), pause: false);
				e.AddPauseReason(DebuggerPauseReason.Exception);
				break;

			case DebugCallbackKind.MDANotification:
				var mdan = (MDANotificationDebugCallbackEventArgs)e;
				objectFactory.CreateException(new DbgExceptionId(PredefinedExceptionCategories.MDA, mdan.CorMDA?.Name ?? "???"), DbgExceptionEventFlags.FirstChance, mdan.CorMDA?.Description, TryGetThread(mdan.CorThread), TryGetModule(null, mdan.CorThread), pause: false);
				e.AddPauseReason(DebuggerPauseReason.Exception);
				break;

			case DebugCallbackKind.ExitProcess:
				// Handled in DnDebugger_OnProcessStateChanged()
				break;

			case DebugCallbackKind.LogMessage:
				var lmsgArgs = (LogMessageDebugCallbackEventArgs)e;
				var msg = lmsgArgs.Message;
				if (msg != null) {
					e.AddPauseReason(DebuggerPauseReason.Other);
					var thread = TryGetThread(lmsgArgs.CorThread);
					SendMessage(new DbgMessageProgramMessage(msg, thread));
				}
				break;

			case DebugCallbackKind.LoadClass:
				var lcArgs = (LoadClassDebugCallbackEventArgs)e;
				var cls = lcArgs.CorClass;
				Debug.Assert(cls != null);
				if (cls != null) {
					var dnModule = dbg.TryGetModule(lcArgs.CorAppDomain, cls);
					if (dnModule?.CorModuleDef != null && dnModule.IsDynamic) {
						var module = TryGetModule(dnModule);
						Debug.Assert(module != null);
						if (module != null)
							ClassLoaded?.Invoke(this, new ClassLoadedEventArgs(module, cls.Token));
					}
				}
				break;
			}
		}

		string TryGetExceptionName(CorValue exObj) => exObj?.ExactType?.Class?.ToReflectionString();

		string TryGetExceptionMessage(CorValue exObj) {
			if (EvalReflectionUtils.ReadExceptionMessage(exObj, out var message))
				return message ?? dnSpy_Debugger_CorDebug_Resources.ExceptionMessageIsNull;
			return null;
		}

		DbgThread TryGetThread(CorThread thread) {
			if (thread == null)
				return null;
			var dnThread = dnDebugger.Processes.FirstOrDefault()?.Threads.FirstOrDefault(a => a.CorThread == thread);
			if (dnThread == null)
				return null;
			DbgEngineThread engineThread;
			lock (lockObj)
				toEngineThread.TryGetValue(dnThread, out engineThread);
			return engineThread?.Thread;
		}

		DbgModule TryGetModule(CorFrame frame, CorThread thread) {
			if (frame == null)
				frame = thread?.ActiveFrame ?? thread?.AllFrames.FirstOrDefault();
			var corModule = frame?.Function?.Module;
			var module = dnDebugger.Modules.FirstOrDefault(a => a.CorModule == corModule);
			if (module == null)
				return null;
			DbgEngineModule engineModule;
			lock (lockObj)
				toEngineModule.TryGetValue(module, out engineModule);
			return engineModule?.Module;
		}

		DbgModule TryGetModule(DnModule dnModule) {
			if (dnModule == null)
				return null;
			lock (lockObj) {
				if (toEngineModule.TryGetValue(dnModule, out var engineModule))
					return engineModule.Module;
			}
			return null;
		}

		void HookDnDebuggerEvents() {
			dnDebugger.DebugCallbackEvent += DnDebugger_DebugCallbackEvent;
			dnDebugger.OnProcessStateChanged += DnDebugger_OnProcessStateChanged;
			dnDebugger.OnNameChanged += DnDebugger_OnNameChanged;
			dnDebugger.OnThreadAdded += DnDebugger_OnThreadAdded;
			dnDebugger.OnAppDomainAdded += DnDebugger_OnAppDomainAdded;
			dnDebugger.OnModuleAdded += DnDebugger_OnModuleAdded;
		}

		void UnhookDnDebuggerEventsAndCloseProcessHandle() {
			if (dnDebugger != null) {
				dnDebugger.DebugCallbackEvent -= DnDebugger_DebugCallbackEvent;
				dnDebugger.OnProcessStateChanged -= DnDebugger_OnProcessStateChanged;
				dnDebugger.OnNameChanged -= DnDebugger_OnNameChanged;
				dnDebugger.OnThreadAdded -= DnDebugger_OnThreadAdded;
				dnDebugger.OnAppDomainAdded -= DnDebugger_OnAppDomainAdded;
				dnDebugger.OnModuleAdded -= DnDebugger_OnModuleAdded;
			}
			hProcess_debuggee?.Close();
		}

		void DnDebugger_OnProcessStateChanged(object sender, DebuggerEventArgs e) {
			Debug.Assert(sender != null && sender == dnDebugger);

			if (dnDebugger.ProcessState == DebuggerProcessState.Terminated) {
				if (hProcess_debuggee == null || hProcess_debuggee.IsClosed || hProcess_debuggee.IsInvalid || !Native.NativeMethods.GetExitCodeProcess(hProcess_debuggee.DangerousGetHandle(), out int exitCode))
					exitCode = -1;
				clrDac = NullClrDac.Instance;
				ClrDacTerminated?.Invoke(this, EventArgs.Empty);
				UnhookDnDebuggerEventsAndCloseProcessHandle();

				SendMessage(new DbgMessageDisconnected(exitCode));
				return;
			}
			else if (dnDebugger.ProcessState == DebuggerProcessState.Paused) {
				ClrDacPaused?.Invoke(this, EventArgs.Empty);
				UpdateThreadProperties_CorDebug();
			}
		}

		void DnDebugger_OnNameChanged(object sender, NameChangedDebuggerEventArgs e) {
			TryGetEngineAppDomain(e.AppDomain)?.UpdateName(e.AppDomain.Name);
			OnNewThreadName_CorDebug(e.Thread);
		}

		DbgEngineAppDomain TryGetEngineAppDomain(DnAppDomain dnAppDomain) {
			if (dnAppDomain == null)
				return null;
			DbgEngineAppDomain engineAppDomain;
			bool b;
			lock (lockObj)
				b = toEngineAppDomain.TryGetValue(dnAppDomain, out engineAppDomain);
			Debug.Assert(b);
			return engineAppDomain;
		}

		void DnDebugger_OnAppDomainAdded(object sender, AppDomainDebuggerEventArgs e) {
			Debug.Assert(objectFactory != null);
			if (e.Added) {
				e.ShouldPause = true;
				var engineAppDomain = objectFactory.CreateAppDomain(e.AppDomain.Name, e.AppDomain.Id, pause: false);
				lock (lockObj)
					toEngineAppDomain.Add(e.AppDomain, engineAppDomain);
			}
			else {
				DbgEngineAppDomain engineAppDomain;
				lock (lockObj) {
					if (toEngineAppDomain.TryGetValue(e.AppDomain, out engineAppDomain)) {
						toEngineAppDomain.Remove(e.AppDomain);
						var appDomain = engineAppDomain.AppDomain;
						foreach (var kv in toEngineThread.ToArray()) {
							if (kv.Value.Thread.AppDomain == appDomain)
								toEngineThread.Remove(kv.Key);
						}
						foreach (var kv in toEngineModule.ToArray()) {
							if (kv.Value.Module.AppDomain == appDomain)
								toEngineModule.Remove(kv.Key);
						}
					}
				}
				if (engineAppDomain != null) {
					e.ShouldPause = true;
					engineAppDomain.Remove();
				}
			}
		}

		sealed class DbgModuleData {
			public DnModule DnModule { get; }
			public ModuleId ModuleId { get; }
			public DbgModuleData(DnModule dnModule, ModuleId moduleId) {
				DnModule = dnModule;
				ModuleId = moduleId;
			}
		}

		void DnDebugger_OnModuleAdded(object sender, ModuleDebuggerEventArgs e) {
			Debug.Assert(objectFactory != null);
			if (e.Added) {
				e.ShouldPause = true;
				var appDomain = TryGetEngineAppDomain(e.Module.AppDomain)?.AppDomain;
				var moduleId = e.Module.DnModuleId.ToModuleId();
				var moduleData = new DbgModuleData(e.Module, moduleId);
				var engineModule = ModuleCreator.CreateModule(objectFactory, appDomain, e.Module, moduleData);
				lock (lockObj) {
					if (!toAssemblyModules.TryGetValue(e.Module.Assembly, out var modules))
						toAssemblyModules.Add(e.Module.Assembly, modules = new List<DnModule>());
					modules.Add(e.Module);
					toEngineModule.Add(e.Module, engineModule);
				}
			}
			else {
				DbgEngineModule engineModule;
				lock (lockObj) {
					if (toAssemblyModules.TryGetValue(e.Module.Assembly, out var modules)) {
						modules.Remove(e.Module);
						if (modules.Count == 0)
							toAssemblyModules.Remove(e.Module.Assembly);
					}
					if (toEngineModule.TryGetValue(e.Module, out engineModule))
						toEngineModule.Remove(e.Module);
				}
				if (engineModule != null) {
					e.ShouldPause = true;
					engineModule.Remove();
				}
			}
		}

		internal CorModuleDef GetDynamicMetadata_EngineThread(DbgModule module) {
			Dispatcher.VerifyAccess();
			if (module == null)
				throw new ArgumentNullException(nameof(module));
			if (!module.TryGetData(out DbgModuleData data))
				return null;
			return data.DnModule.GetOrCreateCorModuleDef();
		}

		internal static ModuleId? TryGetModuleId(DbgModule module) {
			if (module.TryGetData(out DbgModuleData data))
				return data.ModuleId;
			return null;
		}

		void SendMessage(DbgEngineMessage message) => Message?.Invoke(this, message);

		public override void Start(StartDebuggingOptions options) => CorDebugThread(() => {
			if (StartKind == DbgStartKind.Start)
				StartCore((CorDebugStartDebuggingOptions)options);
			else if (StartKind == DbgStartKind.Attach)
				AttachCore((CorDebugAttachDebuggingOptions)options);
			else
				throw new InvalidOperationException();
		});

		protected abstract CLRTypeDebugInfo CreateDebugInfo(CorDebugStartDebuggingOptions options);
		protected abstract CLRTypeAttachInfo CreateAttachInfo(CorDebugAttachDebuggingOptions options);

		void StartCore(CorDebugStartDebuggingOptions options) {
			Dispatcher.VerifyAccess();
			try {
				if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
					throw new InvalidOperationException("Dispatcher has shut down");
				var dbgOptions = new DebugProcessOptions(CreateDebugInfo(options)) {
					DebugMessageDispatcher = new WpfDebugMessageDispatcher(Dispatcher),
					CurrentDirectory = options.WorkingDirectory,
					Filename = options.Filename,
					CommandLine = options.CommandLine,
					BreakProcessKind = options.BreakProcessKind.ToDndbg(),
				};
				dbgOptions.DebugOptions.IgnoreBreakInstructions = options.IgnoreBreakInstructions;

				dnDebugger = DnDebugger.DebugProcess(dbgOptions);
				OnDebugProcess(dnDebugger);
				if (options.DisableManagedDebuggerDetection)
					DisableSystemDebuggerDetection.Initialize(dnDebugger);
				HookDnDebuggerEvents();
				return;
			}
			catch (Exception ex) {
				var cex = ex as COMException;
				const int ERROR_NOT_SUPPORTED = unchecked((int)0x80070032);
				string errMsg;
				if (cex != null && cex.ErrorCode == ERROR_NOT_SUPPORTED)
					errMsg = string.Format(dnSpy_Debugger_CorDebug_Resources.Error_CouldNotStartDebugger, GetIncompatiblePlatformErrorMessage());
				else if (cex != null && cex.ErrorCode == CordbgErrors.CORDBG_E_UNCOMPATIBLE_PLATFORMS)
					errMsg = string.Format(dnSpy_Debugger_CorDebug_Resources.Error_CouldNotStartDebugger, GetIncompatiblePlatformErrorMessage());
				else if (cex != null && cex.ErrorCode == unchecked((int)0x800702E4))
					errMsg = dnSpy_Debugger_CorDebug_Resources.Error_CouldNotStartDebuggerRequireAdminPrivLvl;
				else
					errMsg = string.Format(dnSpy_Debugger_CorDebug_Resources.Error_CouldNotStartDebuggerCheckAccessToFile, options.Filename ?? "<???>", ex.Message);

				SendMessage(new DbgMessageConnected(errMsg));
				return;
			}
		}

		static string GetIncompatiblePlatformErrorMessage() {
			if (IntPtr.Size == 4)
				return dnSpy_Debugger_CorDebug_Resources.UseDnSpyExeToDebug64;
			return dnSpy_Debugger_CorDebug_Resources.UseDnSpy64ExeToDebug32;
		}

		void AttachCore(CorDebugAttachDebuggingOptions options) {
			Dispatcher.VerifyAccess();
			try {
				if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
					throw new InvalidOperationException("Dispatcher has shut down");
				var dbgOptions = new AttachProcessOptions(CreateAttachInfo(options)) {
					DebugMessageDispatcher = new WpfDebugMessageDispatcher(Dispatcher),
					ProcessId = options.ProcessId,
				};

				dnDebugger = DnDebugger.Attach(dbgOptions);
				if (dnDebugger.Processes.Length == 0)
					throw new ErrorException(string.Format(dnSpy_Debugger_CorDebug_Resources.Error_CouldNotAttachToProcess, $"PID={options.ProcessId.ToString()}"));
				OnDebugProcess(dnDebugger);
				HookDnDebuggerEvents();
				return;
			}
			catch (Exception ex) {
				string errMsg;
				if (ex is ErrorException errEx)
					errMsg = errEx.Message;
				else if (CorDebugRuntimeKind == CorDebugRuntimeKind.DotNetCore && ex is ArgumentException) {
					// .NET Core throws ArgumentException if it can't attach to it (.NET Framework throws a COM exception with the correct error message)
					errMsg = string.Format(dnSpy_Debugger_CorDebug_Resources.Error_CouldNotStartDebugger2,
						string.Format(dnSpy_Debugger_CorDebug_Resources.Error_ProcessIsAlreadyBeingDebugged, options.ProcessId.ToString()));
				}
				else
					errMsg = string.Format(dnSpy_Debugger_CorDebug_Resources.Error_CouldNotStartDebugger2, ex.Message);

				SendMessage(new DbgMessageConnected(errMsg));
				return;
			}
		}

		sealed class ErrorException : Exception {
			public ErrorException(string msg) : base(msg) { }
		}

		protected abstract void OnDebugProcess(DnDebugger dnDebugger);

		protected abstract CorDebugRuntimeKind CorDebugRuntimeKind { get; }

		sealed class RuntimeData {
			public DbgEngineImpl Engine { get; }
			public RuntimeData(DbgEngineImpl engine) => Engine = engine;
		}

		internal static DbgEngineImpl TryGetEngine(DbgRuntime runtime) {
			if (runtime.TryGetData(out RuntimeData data))
				return data.Engine;
			return null;
		}

		internal DbgModule[] GetAssemblyModules(DbgModule module) {
			if (!module.TryGetData(out DbgModuleData data))
				return Array.Empty<DbgModule>();
			lock (lockObj) {
				toAssemblyModules.TryGetValue(data.DnModule.Assembly, out var modules);
				if (modules == null || modules.Count == 0)
					return Array.Empty<DbgModule>();
				var res = new List<DbgModule>(modules.Count);
				foreach (var dnModule in modules) {
					if (toEngineModule.TryGetValue(dnModule, out var engineModule))
						res.Add(engineModule.Module);
				}
				return res.ToArray();
			}
		}

		public override void OnConnected(DbgObjectFactory objectFactory, DbgRuntime runtime) {
			Debug.Assert(objectFactory.Runtime == runtime);
			Debug.Assert(Array.IndexOf(objectFactory.Process.Runtimes, runtime) < 0);
			this.objectFactory = objectFactory;
			CorDebugRuntime.Add(new CorDebugRuntimeImpl(runtime, CorDebugRuntimeKind, dnDebugger.DebuggeeVersion ?? string.Empty, dnDebugger.CLRPath, dnDebugger.RuntimeDirectory));
			runtime.GetOrCreateData(() => new RuntimeData(this));
		}

		protected override void CloseCore() {
			UnhookDnDebuggerEventsAndCloseProcessHandle();
			debuggerThread.Terminate();
			lock (lockObj) {
				toEngineAppDomain.Clear();
				toEngineModule.Clear();
				toEngineThread.Clear();
			}
		}

		bool HasConnected_DebugThread {
			get {
				Dispatcher.VerifyAccess();
				// If it's null, we haven't connected yet (most likely due to timeout, eg. trying to debug
				// a .NET Framework program with the .NET Core engine)
				return dnDebugger != null;
			}
		}

		public override void Break() => CorDebugThread(BreakCore);
		void BreakCore() {
			Dispatcher.VerifyAccess();
			if (!HasConnected_DebugThread)
				return;
			if (dnDebugger.ProcessState == DebuggerProcessState.Starting || dnDebugger.ProcessState == DebuggerProcessState.Running) {
				int hr = dnDebugger.TryBreakProcesses();
				if (hr < 0)
					SendMessage(new DbgMessageBreak($"Couldn't break the process, hr=0x{hr:X8}"));
				else {
					Debug.Assert(dnDebugger.ProcessState == DebuggerProcessState.Paused);
					SendMessage(new DbgMessageBreak());
				}
			}
			else
				SendMessage(new DbgMessageBreak());
		}

		public override void Run() => CorDebugThread(RunCore);
		void RunCore() {
			Dispatcher.VerifyAccess();
			if (!HasConnected_DebugThread)
				return;
			if (dnDebugger.ProcessState == DebuggerProcessState.Paused)
				Continue_CorDebug();
		}

		void Continue_CorDebug() {
			Dispatcher.VerifyAccess();
			ClrDacRunning?.Invoke(this, EventArgs.Empty);
			dnDebugger.Continue();
		}

		public override void Terminate() => CorDebugThread(TerminateCore);
		void TerminateCore() {
			Dispatcher.VerifyAccess();
			if (!HasConnected_DebugThread)
				return;
			if (dnDebugger.ProcessState != DebuggerProcessState.Terminated)
				dnDebugger.TerminateProcesses();
		}

		public override bool CanDetach => true;

		public override void Detach() => CorDebugThread(DetachCore);
		void DetachCore() {
			Dispatcher.VerifyAccess();
			if (!HasConnected_DebugThread)
				return;
			if (dnDebugger.ProcessState != DebuggerProcessState.Terminated) {
				int hr = dnDebugger.TryDetach();
				if (hr < 0) {
					Debug.Assert(hr == CordbgErrors.CORDBG_E_UNRECOVERABLE_ERROR);
					dnDebugger.TerminateProcesses();
				}
			}
		}
	}
}
