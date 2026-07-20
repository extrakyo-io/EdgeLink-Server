using System.Text;
using EdgeLink.Infrastructure;
using EdgeLink.NetworkServer.Base.Models;
using EdgeLink.NetworkServer.Services;
using Xunit;

namespace Server.Tests.Unit;

/// <summary>
/// 這組測試守的是同一個結構性錯誤:「讀取失敗 → 當成空的 → 下一次存檔永久覆蓋」。
///
/// 關鍵區別在於「內容毀損」與「讀不到檔」是兩回事:
///   - 內容毀損 → 原檔已無價值,備份後改用預設值是對的(SettingLoader 本來就有做)
///   - 讀不到檔(IO/權限/鎖檔) → 檔案內容**完好無損**,退回預設值再存檔就會毀掉它
/// </summary>
public class ReadFailureDataLossTests : IDisposable
{
    private readonly string _path = Path.Combine(AppPaths.SettingDir, nameof(PortDatas) + ".setting");
    private readonly string? _backup;

    public ReadFailureDataLossTests()
    {
        Directory.CreateDirectory(AppPaths.SettingDir);
        _backup = File.Exists(_path) ? File.ReadAllText(_path, Encoding.UTF8) : null;
    }

    public void Dispose()
    {
        if (_backup != null) File.WriteAllText(_path, _backup, Encoding.UTF8);
        else if (File.Exists(_path)) File.Delete(_path);
        GC.SuppressFinalize(this);
    }

    /// <summary>檔案被別的程序獨占開啟(防毒/備份軟體掃描時的常態)。</summary>
    [Fact]
    public void ReadLockedFile_ThrowsSettingReadException_NotSilentDefault()
    {
        File.WriteAllText(_path, "{\"portDatas\":[{\"Id\":\"aaaaaaaa\",\"ProtocolName\":\"KEEP-ME\"}]}", Encoding.UTF8);

        using var hold = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None);

        // 必須明確區分成「讀取失敗」,而不是回傳空的預設值
        Assert.Throws<SettingReadException>(() => SettingLoader.Load<PortDatas>());
    }

    /// <summary>
    /// 核心回歸:讀取失敗之後,任何存檔都不能落地。
    /// 修正前的行為是 Load 回空清單 → 使用者新增一個埠 → 只有那一筆被寫回去,
    /// 原本所有埠設定永久消失。
    /// </summary>
    [Fact]
    public void SaveAfterReadFailure_DoesNotOverwriteIntactFile()
    {
        const string original = "{\"portDatas\":[{\"Id\":\"aaaaaaaa\",\"ProtocolName\":\"KEEP-ME\"}]}";
        File.WriteAllText(_path, original, Encoding.UTF8);

        var storage = new PortDataStorageService();

        using (new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var loaded = storage.LoadPortData();
            Assert.Empty(loaded);          // 讀不到 → 記憶體是空的(無可避免)
        }

        // 鎖已釋放。使用者在 UI 新增一個埠 → 觸發存檔。
        storage.SavePortData(new List<PortData> { new() { Id = "bbbbbbbb", ProtocolName = "NEW-ONE" } });

        // 檔案內容必須原封不動 —— 寧可存不進去,也不能把完好的設定蓋掉
        Assert.Equal(original, File.ReadAllText(_path, Encoding.UTF8));
    }

    /// <summary>
    /// 對照組:內容毀損時的既有行為必須保留 —— 備份成 .corrupt-* 後回預設值,
    /// 而且此後的存檔要正常運作(否則使用者永遠救不回來)。
    /// </summary>
    [Fact]
    public void CorruptContent_BacksUpAndStillAllowsSave()
    {
        File.WriteAllText(_path, "{ this is not valid json", Encoding.UTF8);

        var storage = new PortDataStorageService();
        Assert.Empty(storage.LoadPortData());

        var backups = Directory.GetFiles(AppPaths.SettingDir, nameof(PortDatas) + ".setting.corrupt-*");
        Assert.NotEmpty(backups);
        foreach (var b in backups) File.Delete(b);

        storage.SavePortData(new List<PortData> { new() { Id = "cccccccc", ProtocolName = "AFTER-CORRUPT" } });
        Assert.Contains("AFTER-CORRUPT", File.ReadAllText(_path, Encoding.UTF8));
    }

    /// <summary>AtomicFile 不可留下暫存檔,也不可在覆寫途中讓檔案短暫變成空的。</summary>
    [Fact]
    public void AtomicWrite_ReplacesContentAndLeavesNoTempFile()
    {
        string p = Path.Combine(AppPaths.SettingDir, "atomic-test.tmpfile");
        try
        {
            AtomicFile.WriteAllText(p, "first");
            Assert.Equal("first", File.ReadAllText(p, Encoding.UTF8));

            AtomicFile.WriteAllText(p, "second");
            Assert.Equal("second", File.ReadAllText(p, Encoding.UTF8));
            Assert.False(File.Exists(p + ".tmp"), "暫存檔沒有被清掉");
        }
        finally
        {
            if (File.Exists(p))          File.Delete(p);
            if (File.Exists(p + ".tmp")) File.Delete(p + ".tmp");
        }
    }
}
