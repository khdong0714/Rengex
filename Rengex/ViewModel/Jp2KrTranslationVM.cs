﻿namespace Rengex {
  using Nanikit.Ehnd;
  using Rengex.Translator;
  using System;
  using System.Collections.Generic;
  using System.Collections.ObjectModel;
  using System.Linq;
  using System.Management;
  using System.Text.RegularExpressions;
  using System.Threading.Tasks;
  using System.Windows.Media;

  public class Jp2KrDesignVM : Jp2KrTranslationVM {
    public class TestLabelProgressVM : ILabelProgressVM {
      public string Label => "테스트";

      public double Value => 50;

      public Brush Foreground => new SolidColorBrush(Colors.PaleGreen);

      public void Cancel() {
      }
    }
    public new TestLabelProgressVM Progress { get; set; }

    public Jp2KrDesignVM() : base(null, null) {
      Ongoings.Add(new TestLabelProgressVM());
      Ongoings.Add(new TestLabelProgressVM());
      Faults.Add(new TestLabelProgressVM());
      Progress = new TestLabelProgressVM();
    }
  }

  public class Jp2KrTranslationVM : ViewModelBase {
    private static readonly int coreCount;

    public ObservableCollection<ILabelProgressVM> Ongoings { get; private set; }
    public ObservableCollection<ILabelProgressVM> Faults { get; private set; }
    public List<Exception> Exceptions { get; private set; } = new List<Exception>();

    public LabelProgressVM Progress { get; private set; }

    private readonly RegexDotConfiguration dotConfig;
    private readonly List<TranslationUnit>? translations;
    private string? workKind;
    private EhndTranslator? _selfTranslator;

    static Jp2KrTranslationVM() {
      coreCount = GetCoreCount();
    }


    /// <summary>
    /// if paths is null, search from metadata folder.
    /// </summary>
    public Jp2KrTranslationVM(RegexDotConfiguration dot, string[]? paths = null) {
      dotConfig = dot;
      translations = paths?.SelectMany(p => WalkForSources(p))?.ToList();
      Progress = new LabelProgressVM();
      Faults = new ObservableCollection<ILabelProgressVM>();
      Ongoings = new ObservableCollection<ILabelProgressVM>();
    }

    public Task ImportTranslation() {
      workKind = "추출: ";
      return ParallelForEach(x => new ImportJp2Kr(x));
    }

    public async Task MachineTranslation() {
      workKind = "번역: ";
      if (_selfTranslator == null) {
        _selfTranslator = new EhndTranslator(Properties.Settings.Default.EzTransDir);
      }

      using var engine = new ForkTranslator(coreCount, _selfTranslator);

      Jp2KrWork genVm(TranslationUnit x) {
        return new TranslateJp2Kr(x, engine);
      }

      await ParallelForEach(genVm).ConfigureAwait(false);
    }

    public Task ExportTranslation() {
      workKind = "병합: ";
      return ParallelForEach(x => new MergeJp2Kr(x));
    }

    public async Task OnestopTranslation() {
      workKind = "원터치: ";
      if (_selfTranslator == null) {
        _selfTranslator = new EhndTranslator(Properties.Settings.Default.EzTransDir);
      }

      using var engine = new ForkTranslator(coreCount, _selfTranslator);

      Jp2KrWork genVm(TranslationUnit x) {
        return new OnestopJp2Kr(x, engine);
      }

      await ParallelForEach(genVm).ConfigureAwait(false);
    }

    private static int GetCoreCount() {
      int coreCount = 0;
      foreach (var item in new ManagementObjectSearcher("Select * from Win32_Processor").Get()) {
        string row = $"{item["NumberOfCores"]}";
        if (int.TryParse(row, out int count)) {
          coreCount += count;
        }
      }
      return coreCount;
    }

    private IEnumerable<TranslationUnit> WalkForSources(string path) {
      return ManagedPath
        .WalkForSources(path)
        .Select(x => new TranslationUnit(dotConfig, x));
    }

    private IEnumerable<TranslationUnit> FindTranslations() {
      return WalkForSources(ManagedPath.MetadataDirectory);
    }

    private Task ParallelForEach(Func<TranslationUnit, Jp2KrWork> genViewModel) {
      List<TranslationUnit> transUnits = translations ?? FindTranslations().ToList();
      int complete = 0;
      Progress.Value = 0;
      Progress.Label = $"{workKind}{complete} / {transUnits.Count}";

      return transUnits.ForEachPinnedAsync(coreCount, async t => {
        Jp2KrWork item = genViewModel(t);
        Ongoings.Add(item.Progress);
        try {
          await Task.Run(() => item.Process());
        }
        catch (EhndNotFoundException) {
          Progress.Foreground = LabelProgressVM.FgError;
          throw;
        }
        catch (Exception e) {
          Progress.Foreground = LabelProgressVM.FgError;
          string msg = e is RegexMatchTimeoutException
            ? "정규식 검색이 너무 오래 걸립니다. 정규식을 점검해주세요."
            : e.Message;
          item.SetProgress(TranslationPhase.Error, 100, msg);
          Faults.Add(item.Progress);
          Exceptions.Add(e);
        }
        finally {
          _ = Ongoings.Remove(item.Progress);
          complete++;
          Progress.Value = (double)complete / transUnits.Count * 100;
          Progress.Label = $"{workKind}{complete} / {transUnits.Count}";
        }
      });
    }
  }
}
