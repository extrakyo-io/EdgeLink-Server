using System.Threading;
using EdgeLink.Infrastructure;
using EdgeLink.NetworkServer.Services;

namespace EdgeLink.Mask;

public class MaskDefinitionManager
{
    private static MaskDefinitionManager? _instance;
    public static MaskDefinitionManager Instance => _instance ??= new MaskDefinitionManager();

    private const string DefaultMaskId = "OriginalData";

    private readonly MaskDefinitionStorageService _storage = new();
    private readonly Dictionary<string, MaskDefinition> _definitions = new();
    private readonly List<string> _order = new();
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);

    public event Action? OnMaskTypesChanged;

    private MaskDefinitionManager()
    {
        Load();
        if (!_definitions.ContainsKey(DefaultMaskId))
        {
            AddDefinitionInternal(new MaskDefinition
            {
                maskId = DefaultMaskId,
                localizationKey = DefaultMaskId,
                description = "Forward raw data as-is",
                outputTemplate = "{raw}"
            });
            Save();
        }
    }

    private void Load()
    {
        var data = _storage.Load();
        _lock.EnterWriteLock();
        try
        {
            foreach (var def in data.definitions)
            {
                if (string.IsNullOrEmpty(def.maskId)) continue;
                _definitions[def.maskId] = def;
                _order.Add(def.maskId);
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    private void Save()
    {
        MaskDefinitions data;
        _lock.EnterReadLock();
        try
        {
            data = new MaskDefinitions
            {
                definitions = _order
                    .Where(id => _definitions.ContainsKey(id))
                    .Select(id => _definitions[id])
                    .ToList()
            };
        }
        finally { _lock.ExitReadLock(); }
        _storage.Save(data);
    }

    private void AddDefinitionInternal(MaskDefinition def)
    {
        _definitions[def.maskId] = def;
        if (!_order.Contains(def.maskId))
            _order.Add(def.maskId);
    }

    public void AddMaskType(string maskId, string? localizationKey = null)
    {
        if (string.IsNullOrEmpty(maskId)) { AppLogger.Warning("[MaskDefinitionManager] maskId cannot be empty"); return; }
        _lock.EnterWriteLock();
        try
        {
            if (_definitions.ContainsKey(maskId)) { AppLogger.Warning($"[MaskDefinitionManager] Mask '{maskId}' already exists"); return; }
            AddDefinitionInternal(new MaskDefinition
            {
                maskId = maskId,
                localizationKey = string.IsNullOrEmpty(localizationKey) ? maskId : localizationKey,
                description = "",
                fieldDelimiter = ";",
                kvSeparator = ":",
                outputTemplate = "{raw}"
            });
        }
        finally { _lock.ExitWriteLock(); }
        Save();
        OnMaskTypesChanged?.Invoke();
    }

    public void RemoveMaskType(string maskId)
    {
        if (maskId == DefaultMaskId) { AppLogger.Warning($"[MaskDefinitionManager] Cannot delete default mask '{DefaultMaskId}'"); return; }
        _lock.EnterWriteLock();
        try
        {
            if (!_definitions.ContainsKey(maskId)) { AppLogger.Warning($"[MaskDefinitionManager] Mask '{maskId}' not found"); return; }
            _definitions.Remove(maskId);
            _order.Remove(maskId);
        }
        finally { _lock.ExitWriteLock(); }
        Save();
        OnMaskTypesChanged?.Invoke();
    }

    public void RenameMask(string oldId, string newId)
    {
        if (oldId == DefaultMaskId) throw new InvalidOperationException($"Cannot rename default mask '{DefaultMaskId}'");
        if (string.IsNullOrWhiteSpace(newId)) throw new ArgumentException("New name cannot be empty");
        _lock.EnterWriteLock();
        try
        {
            if (!_definitions.ContainsKey(oldId)) throw new KeyNotFoundException($"Mask '{oldId}' not found");
            if (_definitions.ContainsKey(newId)) throw new InvalidOperationException($"Mask '{newId}' already exists");
            var def = _definitions[oldId];
            def.maskId = newId;
            if (def.localizationKey == oldId) def.localizationKey = newId;
            _definitions.Remove(oldId);
            int idx = _order.IndexOf(oldId);
            if (idx >= 0) _order[idx] = newId; else _order.Add(newId);
            _definitions[newId] = def;
        }
        finally { _lock.ExitWriteLock(); }
        Save();
        OnMaskTypesChanged?.Invoke();
    }

    public void SaveDefinition(MaskDefinition def)
    {
        if (def == null || string.IsNullOrEmpty(def.maskId)) return;
        _lock.EnterWriteLock();
        try { AddDefinitionInternal(def); }
        finally { _lock.ExitWriteLock(); }
        Save();
        OnMaskTypesChanged?.Invoke();
    }

    public MaskDefinition? GetDefinition(string maskId)
    {
        _lock.EnterReadLock();
        try { _definitions.TryGetValue(maskId ?? "", out var def); return def; }
        finally { _lock.ExitReadLock(); }
    }

    public bool HasMaskType(string maskId)
    {
        _lock.EnterReadLock();
        try { return _definitions.ContainsKey(maskId ?? ""); }
        finally { _lock.ExitReadLock(); }
    }

    public List<string> GetMaskTypeIds()
    {
        _lock.EnterReadLock();
        try { return new List<string>(_order); }
        finally { _lock.ExitReadLock(); }
    }

    public string GetLocalizationKey(string maskId)
    {
        _lock.EnterReadLock();
        try { return _definitions.TryGetValue(maskId, out var def) ? def.localizationKey : maskId; }
        finally { _lock.ExitReadLock(); }
    }

    public int GetCount()
    {
        _lock.EnterReadLock();
        try { return _definitions.Count; }
        finally { _lock.ExitReadLock(); }
    }

    public void NotifyChanged() => OnMaskTypesChanged?.Invoke();
}
