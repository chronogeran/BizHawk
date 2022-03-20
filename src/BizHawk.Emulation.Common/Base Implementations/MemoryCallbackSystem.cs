﻿#nullable disable

using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;

namespace BizHawk.Emulation.Common
{
	/// <summary>
	/// This is a generic implementation of IMemoryCallbackSystem
	/// that can be used by used by any core
	/// </summary>
	/// <seealso cref="IMemoryCallbackSystem" />
	public class MemoryCallbackSystem : IMemoryCallbackSystem
	{
		public MemoryCallbackSystem(string[] availableScopes)
		{
			availableScopes ??= new[] { "System Bus" };

			AvailableScopes = availableScopes;
			ExecuteCallbacksAvailable = true;

			_reads.CollectionChanged += OnCollectionChanged;
			_writes.CollectionChanged += OnCollectionChanged;
			_execs.CollectionChanged += OnCollectionChanged;
		}

		private readonly ObservableCollection<IMemoryCallback> _reads = new ObservableCollection<IMemoryCallback>();
		private readonly ObservableCollection<IMemoryCallback> _writes = new ObservableCollection<IMemoryCallback>();
		private readonly ObservableCollection<IMemoryCallback> _execs = new ObservableCollection<IMemoryCallback>();

		private readonly Dictionary<string, Dictionary<uint, List<IMemoryCallback>>> _readsByScopeAndAddress = new();
		private readonly Dictionary<string, Dictionary<uint, List<IMemoryCallback>>> _writesByScopeAndAddress = new();
		private readonly Dictionary<string, Dictionary<uint, List<IMemoryCallback>>> _execsByScopeAndAddress = new();
		private readonly List<IMemoryCallback> _nullAddressReads = new();
		private readonly List<IMemoryCallback> _nullAddressWrites = new();
		private readonly List<IMemoryCallback> _nullAddressExecs = new();

		private bool _hasAny;

		public bool ExecuteCallbacksAvailable { get; }

		public string[] AvailableScopes { get; }

		/// <exception cref="InvalidOperationException">scope of <paramref name="callback"/> isn't available</exception>
		public void Add(IMemoryCallback callback)
		{
			if (!AvailableScopes.Contains(callback.Scope))
			{
				throw new InvalidOperationException($"{callback.Scope} is not currently supported for callbacks");
			}

			switch (callback.Type)
			{
				case MemoryCallbackType.Execute:
					_execs.Add(callback);
					if (callback.Address == null)
						_nullAddressExecs.Add(callback);
					else
						AddToDictionary(_execsByScopeAndAddress, callback);
					break;
				case MemoryCallbackType.Read:
					_reads.Add(callback);
					if (callback.Address == null)
						_nullAddressReads.Add(callback);
					else
						AddToDictionary(_readsByScopeAndAddress, callback);
					break;
				case MemoryCallbackType.Write:
					_writes.Add(callback);
					if (callback.Address == null)
						_nullAddressWrites.Add(callback);
					else
						AddToDictionary(_writesByScopeAndAddress, callback);
					break;
			}

			if (UpdateHasVariables())
			{
				Changes();
			}
		}

		private void AddToDictionary(Dictionary<string, Dictionary<uint, List<IMemoryCallback>>> dict, IMemoryCallback callback)
		{
			var scope = callback.Scope ?? "";
			if (!dict.ContainsKey(scope))
				dict.Add(scope, new Dictionary<uint, List<IMemoryCallback>>());
			if (!dict[scope].ContainsKey(callback.Address.Value))
				dict[scope].Add(callback.Address.Value, new List<IMemoryCallback>());
			dict[scope][callback.Address.Value].Add(callback);
		}

		private static void Call(ObservableCollection<IMemoryCallback> cbs, uint addr, uint value, uint flags, string scope)
		{
			// ReSharper disable once ForCanBeConvertedToForeach
			// Intentionally a for loop - https://github.com/TASEmulators/BizHawk/issues/1823
			for (int i = 0; i < cbs.Count; i++)
			{
				if (!cbs[i].Address.HasValue || (cbs[i].Scope == scope && cbs[i].Address == (addr & cbs[i].AddressMask)))
				{
					cbs[i].Callback(addr, value, flags);
				}
			}
		}

		private static void Call(Dictionary<string, Dictionary<uint, List<IMemoryCallback>>> dict, List<IMemoryCallback> nulls, uint addr, uint value, uint flags, string scope)
		{
			// Always call nulls
			for (int i = 0; i < nulls.Count; i++)
				nulls[i].Callback(addr, value, flags);
			// Null scopes always called for the address
			if (dict.ContainsKey(string.Empty) && dict[string.Empty].ContainsKey(addr))
				for (int i = 0; i < dict[string.Empty][addr].Count; i++)
					dict[string.Empty][addr][i].Callback(addr, value, flags);
			if (dict.ContainsKey(scope) && dict[scope].ContainsKey(addr))
			{
				for (int i = 0; i < dict[scope][addr].Count; i++)
					dict[scope][addr][i].Callback(addr, value, flags);
			}
		}

		public void CallMemoryCallbacks(uint addr, uint value, uint flags, string scope)
		{
			if (!_hasAny)
			{
				return;
			}

			if (HasReads)
			{
				if ((flags & (uint)MemoryCallbackFlags.AccessRead) != 0)
				{
					//Call(_reads, addr, value, flags, scope);
					Call(_readsByScopeAndAddress, _nullAddressReads, addr, value, flags, scope);
				}
			}

			if (HasWrites)
			{
				if ((flags & (uint)MemoryCallbackFlags.AccessWrite) != 0)
				{
					Call(_writesByScopeAndAddress, _nullAddressWrites, addr, value, flags, scope);
				}
			}

			if (HasExecutes)
			{
				if ((flags & (uint)MemoryCallbackFlags.AccessExecute) != 0)
				{
					Call(_execsByScopeAndAddress, _nullAddressExecs, addr, value, flags, scope);
				}
			}
		}

		public bool HasReads { get; private set; }

		public bool HasWrites { get; private set; }

		public bool HasExecutes { get; private set; }

		public bool HasReadsForScope(string scope)
		{
			return _reads.Any(e => e.Scope == scope);
		}

		public bool HasWritesForScope(string scope)
		{
			return _writes.Any(e => e.Scope == scope);
		}

		public bool HasExecutesForScope(string scope)
		{
			return _execs.Any(e => e.Scope == scope);
		}

		private bool UpdateHasVariables()
		{
			bool hadReads = HasReads;
			bool hadWrites = HasWrites;
			bool hadExecutes = HasExecutes;

			HasReads = _reads.Count > 0;
			HasWrites = _writes.Count > 0;
			HasExecutes = _execs.Count > 0;
			_hasAny = HasReads || HasWrites || HasExecutes;

			return HasReads != hadReads || HasWrites != hadWrites || HasExecutes != hadExecutes;
		}

		private int RemoveInternal(MemoryCallbackDelegate action)
		{
			var readsToRemove = _reads.Where(imc => imc.Callback == action).ToList();
			var writesToRemove = _writes.Where(imc => imc.Callback == action).ToList();
			var execsToRemove = _execs.Where(imc => imc.Callback == action).ToList();

			foreach (var read in readsToRemove)
			{
				_reads.Remove(read);
				if (read.Address == null)
					_nullAddressReads.Remove(read);
				else
					_readsByScopeAndAddress[read.Scope ?? ""][read.Address.Value].Remove(read);
			}

			foreach (var write in writesToRemove)
			{
				_writes.Remove(write);
				if (write.Address == null)
					_nullAddressWrites.Remove(write);
				else
					_writesByScopeAndAddress[write.Scope ?? ""][write.Address.Value].Remove(write);
			}

			foreach (var exec in execsToRemove)
			{
				_execs.Remove(exec);
				if (exec.Address == null)
					_nullAddressExecs.Remove(exec);
				else
					_execsByScopeAndAddress[exec.Scope ?? ""][exec.Address.Value].Remove(exec);
			}

			return readsToRemove.Count + writesToRemove.Count + execsToRemove.Count;
		}

		public void Remove(MemoryCallbackDelegate action)
		{
			if (RemoveInternal(action) > 0)
			{
				if (UpdateHasVariables())
				{
					Changes();
				}
			}
		}

		public void RemoveAll(IEnumerable<MemoryCallbackDelegate> actions)
		{
			bool changed = false;
			foreach (var action in actions)
			{
				changed |= RemoveInternal(action) > 0;
			}

			if (changed)
			{
				if (UpdateHasVariables())
				{
					Changes();
				}
			}
		}

		public void Clear()
		{
			// Remove one-by-one to avoid NotifyCollectionChangedAction.Reset events.
			for (int i = _reads.Count - 1; i >= 0; i--)
			{
				_reads.RemoveAt(i);
			}

			for (int i = _writes.Count - 1; i >= 0; i--)
			{
				_writes.RemoveAt(i);
			}

			for (int i = _execs.Count - 1; i >= 0; i--)
			{
				_execs.RemoveAt(i);
			}
			_nullAddressReads.Clear();
			_nullAddressWrites.Clear();
			_nullAddressExecs.Clear();
			_readsByScopeAndAddress.Clear();
			_writesByScopeAndAddress.Clear();
			_execsByScopeAndAddress.Clear();

			if (UpdateHasVariables())
			{
				Changes();
			}
		}

		public delegate void ActiveChangedEventHandler();
		public event ActiveChangedEventHandler ActiveChanged;

		public delegate void CallbackAddedEventHandler(IMemoryCallback callback);
		public event CallbackAddedEventHandler CallbackAdded;

		public delegate void CallbackRemovedEventHandler(IMemoryCallback callback);
		public event CallbackRemovedEventHandler CallbackRemoved;

		private void Changes()
		{
			ActiveChanged?.Invoke();
		}

		public void OnCollectionChanged(object sender, NotifyCollectionChangedEventArgs args)
		{
			switch (args.Action)
			{
				case NotifyCollectionChangedAction.Add:
					foreach (IMemoryCallback callback in args.NewItems)
					{
						CallbackAdded?.Invoke(callback);
					}

					break;
				case NotifyCollectionChangedAction.Remove:
					foreach (IMemoryCallback callback in args.OldItems)
					{
						CallbackRemoved?.Invoke(callback);
					}

					break;
			}
		}

		public IEnumerator<IMemoryCallback> GetEnumerator()
		{
			foreach (var imc in _reads)
			{
				yield return imc;
			}

			foreach (var imc in _writes)
			{
				yield return imc;
			}

			foreach (var imc in _execs)
			{
				yield return imc;
			}
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			foreach (var imc in _reads)
			{
				yield return imc;
			}

			foreach (var imc in _writes)
			{
				yield return imc;
			}

			foreach (var imc in _execs)
			{
				yield return imc;
			}
		}
	}

	public class MemoryCallback : IMemoryCallback
	{
		public MemoryCallback(string scope, MemoryCallbackType type, string name, MemoryCallbackDelegate callback, uint? address, uint? mask)
		{
			Type = type;
			Name = name;
			Callback = callback;
			Address = address;
			AddressMask = mask ?? 0xFFFFFFFF;
			Scope = scope;
		}

		public MemoryCallbackType Type { get; }
		public string Name { get; }
		public MemoryCallbackDelegate Callback { get; }
		public uint? Address { get; }
		public uint? AddressMask { get; }
		public string Scope { get; }
	}
}