namespace Rengex {
  using System;
  using System.Collections.Generic;
  using System.Globalization;
  using System.IO;
  using System.Linq;
  using System.Threading;
  using System.Threading.Tasks;
  using System.Threading.Tasks.Dataflow;

  internal interface IDotConfig<T> {
    /// <summary>
    /// 빌더 메소드. 설정 파일 경로를 주면 설정을 제공.
    /// </summary>
    /// <param name="path">설정 파일 경로</param>
    /// <returns>설정 인스턴스</returns>
    T CreateFromFile(string path);
    /// <returns>기본 설정 파일 내용</returns>
    string GetDefaultConfig();
    /// <returns>설정 파일의 확장자</returns>
    string GetExtension();
    /// <summary>
    /// 다른 설정파일을 제공
    /// </summary>
    Func<string, T> ConfigResolver { set; }
  }

  internal interface IRegion {
    string Origin { get; }
  }

  internal class FileTimeoutWatcher : IDisposable {

    public event FileSystemEventHandler Commited = delegate { };

    private readonly BufferBlock<FileSystemEventArgs> ChangeQueue
      = new BufferBlock<FileSystemEventArgs>();
    private readonly CancellationTokenSource Cancel = new CancellationTokenSource();
    private readonly Task TimeoutFilter;
    private readonly int MsTimeout;

    public FileTimeoutWatcher(int msTimeout) {
      MsTimeout = msTimeout;
    }

    public FileTimeoutWatcher(int msTimeout, params FileSystemWatcher[] watchers) : this(msTimeout) {
      TimeoutFilter = DispatchDeduplicatedEvents();
      foreach (FileSystemWatcher watcher in watchers) {
        Subscribe(watcher);
      }
    }

    public void Subscribe(FileSystemWatcher watcher) {
      watcher.Created += ConfigChanged;
      watcher.Deleted += ConfigChanged;
      watcher.Changed += ConfigChanged;
      watcher.Renamed += ConfigChanged;
    }

    private void ConfigChanged(object sender, FileSystemEventArgs fsEvent) {
      ChangeQueue.Post(fsEvent);
    }

    private async Task DispatchDeduplicatedEvents() {
      while (true) {
        foreach (var fse in await GetDeduplicatedEventsTimeout()) {
          Commited.Invoke(this, fse);
        }
      }
    }

    private async Task<IEnumerable<FileSystemEventArgs>> GetDeduplicatedEventsTimeout() {
      var changes = new Dictionary<string, FileSystemEventArgs>();
      var fse = await ChangeQueue.ReceiveAsync(Cancel.Token).ConfigureAwait(false);
      changes[fse.FullPath] = fse;
      var timeout = Task.Delay(MsTimeout);
      while (true) {
        var fetch = ChangeQueue.ReceiveAsync(Cancel.Token);
        Task fin = await Task.WhenAny(fetch, timeout).ConfigureAwait(false);
        if (fin == timeout) {
          break;
        }
        else {
          changes[fse.FullPath] = await fetch.ConfigureAwait(false);
        }
      }

      return changes.Values;
    }

    public void Dispose() {
      Dispose(true);
    }

    protected void Dispose(bool disposing) {
      if (disposing) {
        Cancel.Cancel();
        Cancel.Dispose();
      }
    }
  }

  /// <summary>
  /// 경로에 따라 실제 적용되는 설정을 반환.
  /// </summary>
  /// <typeparam name="T">설정 클래스</typeparam>
  internal class DotConfigurator<T> : IDisposable where T : IDotConfig<T>, new() {

    public event Action<FileSystemEventArgs> ConfigReloaded = delegate { };
    public event Action<FileSystemEventArgs, Exception> Faulted = delegate { };
    public IEnumerable<string> RegionPaths => Regions.Select(x => x.ToString());

    /// <summary>
    /// 설정 변경을 탐지하기 위한 감시자.
    /// </summary>
    private readonly FileTimeoutWatcher TimeoutWatcher;

    private readonly T ConfigBuilder;
    private readonly string RootPath;
    private readonly List<Region> Regions = new();
    private readonly Dictionary<string, Region> RegionDict = new();

    public class Region : IComparer<Region> {
      public string Prefix;
      public string Suffix;
      public T Value;
      public int Priority => Prefix.Length * 1000 + Suffix.Length;
      public FileSystemWatcher Watcher;

      public Region(T value) {
        Prefix = "";
        Suffix = "";
        Value = value;
      }

      public Region(string pattern, T value) {
        Value = value;
        Prefix = Path.GetDirectoryName(pattern).ToLower() + '\\';
        int extIdx = pattern.Length - value.GetExtension().Length;
        Suffix = Path.GetFileName(pattern.Remove(extIdx)).ToLower();
      }

      public int Compare(Region x, Region y) {
        return -(x.Priority - y.Priority);
      }

      public override string ToString() => Prefix + Suffix + Value.GetExtension();
    }

    /// <summary>
    /// 해당 경로의 모든 설정을 불러오고 변경을 감시함.
    /// </summary>
    /// <param name="path">설정을 찾을 최상위 경로</param>
    public DotConfigurator(
      string path,
      Action<FileSystemEventArgs> reloaded,
      Action<FileSystemEventArgs, Exception> faulted) {

      RootPath = Path.GetFullPath(path);
      ConfigReloaded += reloaded;
      Faulted += faulted;
      ConfigBuilder = new T();
      TimeoutWatcher = GetTimeoutWatcher();
      TimeoutWatcher.Commited += FileChanged;
      CacheAllConfigs();
    }

    private FileTimeoutWatcher GetTimeoutWatcher() {
      var watcher = new FileSystemWatcher(RootPath, "*" + ConfigBuilder.GetExtension()) {
        IncludeSubdirectories = true,
        EnableRaisingEvents = true
      };
      return new FileTimeoutWatcher(100, watcher);
    }

    private void CacheAllConfigs() {
      string extension = ConfigBuilder.GetExtension();
      WriteDefaultConfig(extension);

      Regions.Add(new Region(ConfigBuilder));
      foreach (string config in Directory.EnumerateFiles(RootPath, '*' + extension, SearchOption.AllDirectories)) {
        try {
          AddRegion(config);
        }
        catch (Exception exception) {
          var type = WatcherChangeTypes.Created;
          string dir = Path.GetDirectoryName(config);
          string name = Path.GetFileName(config);
          var eventArgs = new FileSystemEventArgs(type, dir, name);
          Faulted.Invoke(eventArgs, exception);
          return;
        }
      }
    }

    private T ResolveConfig(string path) {
      return RegionDict[path.ToLower()].Value;
    }

    private void WriteDefaultConfig(string extension) {
      string top = Path.Combine(RootPath, extension);
      if (!File.Exists(top)) {
        Util.PrecreateDirectory(top);
        File.WriteAllText(top, ConfigBuilder.GetDefaultConfig());
      }
    }

    public T GetConfiguration(string path) {
      string absolute = Path.GetFullPath(path).ToLower(CultureInfo.InvariantCulture);
      return Regions.First(x =>
        absolute.EndsWith(x.Suffix, StringComparison.InvariantCultureIgnoreCase) &&
        absolute.StartsWith(x.Prefix, StringComparison.InvariantCultureIgnoreCase)
      ).Value;
    }

    private void FileChanged(object sender, FileSystemEventArgs fse) {
      if (Regions == null) {
        return;
      }
      try {
        ReflectDelta(fse);
        ConfigReloaded.Invoke(fse);
      }
      catch (Exception e) {
        Faulted.Invoke(fse, e);
      }
    }

    private void ReflectDelta(FileSystemEventArgs eventArgs) {
      switch (eventArgs.ChangeType) {
      case WatcherChangeTypes.Changed:
        if (RegionDict.TryGetValue(eventArgs.FullPath.ToLower(), out var region)) {
          region.Value = ConfigBuilder.CreateFromFile(eventArgs.FullPath);
        }
        else {
          AddRegion(eventArgs.FullPath);
        }
        break;
      case WatcherChangeTypes.Deleted:
        RemoveRegion(eventArgs.FullPath);
        break;
      case WatcherChangeTypes.Created:
        AddRegion(eventArgs.FullPath);
        break;
      case WatcherChangeTypes.Renamed:
        var renameArgs = eventArgs as RenamedEventArgs;
        RemoveRegion(renameArgs.OldFullPath);
        if (renameArgs.FullPath.EndsWith(ConfigBuilder.GetExtension())) {
          AddRegion(renameArgs.FullPath);
        }
        break;
      }
    }

    private void AddRegion(string path) {
      var cfg = ConfigBuilder.CreateFromFile(path);
      cfg.ConfigResolver = ResolveConfig;
      var region = new Region(path, cfg) {
        Watcher = new FileSystemWatcher(Path.GetDirectoryName(path), Path.GetFileName(path))
      };
      region.Watcher.EnableRaisingEvents = true;
      TimeoutWatcher.Subscribe(region.Watcher);
      int idx = Regions.BinarySearch(region, region);
      if (idx < 0) {
        idx = ~idx;
      }
      Regions.Insert(idx, region);
      RegionDict[path.ToLower()] = region;
    }

    private void RemoveRegion(string path) {
      if (RegionDict.TryGetValue(path.ToLower(), out var region)) {
        region.Watcher?.Dispose();
        Regions.Remove(region);
        RegionDict.Remove(path);
      }
    }

    public void Dispose() {
      Dispose(true);
    }

    protected void Dispose(bool disposing) {
      if (disposing) {
        TimeoutWatcher.Dispose();
      }
    }
  }

  public class RegexDotConfiguration {
    public event Action<FileSystemEventArgs> ConfigReloaded = delegate { };
    public event Action<FileSystemEventArgs, Exception> ConfigFaulted = delegate { };

    private readonly DotConfigurator<MatchConfig> Matcher;
    private readonly DotConfigurator<ReplaceConfig> Replacer;

    public IEnumerable<string> RegionPaths => Matcher.RegionPaths.Concat(Replacer.RegionPaths);

    public RegexDotConfiguration(
      string projectDir,
      Action<FileSystemEventArgs> reloaded,
      Action<FileSystemEventArgs, Exception> faulted
    ) {
      Directory.CreateDirectory(projectDir);
      ConfigReloaded += reloaded;
      ConfigFaulted += faulted;
      Matcher = new DotConfigurator<MatchConfig>(projectDir, Reloaded, Faulted);
      Replacer = new DotConfigurator<ReplaceConfig>(projectDir, Reloaded, Faulted);
    }

    public RegexConfiguration GetConfiguration(string path) {
      MatchConfig matcher = Matcher.GetConfiguration(path);
      ReplaceConfig replacer = Replacer.GetConfiguration(path);
      return new RegexConfiguration(matcher, replacer);
    }

    private void Reloaded(FileSystemEventArgs obj) {
      ConfigReloaded.Invoke(obj);
    }

    private void Faulted(FileSystemEventArgs fse, Exception e) {
      ConfigFaulted.Invoke(fse, e);
    }
  }
}
