﻿using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Rengex {

  /// <summary>
  /// It can translate japanese to korean.
  /// </summary>
  public interface IJp2KrTranslator : IDisposable {
    /// <summary>
    /// Translate japanese string to korean.
    /// </summary>
    /// <param name="source">Japanese string</param>
    /// <returns>Korean string</returns>
    Task<string> Translate(string source);
  }

  public class EzTransNotFoundException : ApplicationException {
    public override string Message => "이지트랜스를 찾지 못했습니다.";
  }

  public class EzTransXp : IJp2KrTranslator {

    private static string GetDllPath(string eztPath) {
      return Path.Combine(eztPath, "J2KEngine.dll");
    }

    public readonly Task InitDll;

    private IntPtr EzTransDll;
    private J2K_FreeMem J2kFree;
    private J2K_TranslateMMNTW J2kMmntw;

    public EzTransXp(string eztPath = null, int msDelay = 200) {
      if (string.IsNullOrWhiteSpace(eztPath)) {
        eztPath = GetEztransDirFromReg();
      }
      if (eztPath == null || !File.Exists(GetDllPath(eztPath))) {
        throw new EzTransNotFoundException();
      }
      InitDll = LoadNativeDll(eztPath, msDelay);
    }

    public async Task<bool> IsHdorEnabled() {
      string chk = await Translate("蜜ドル辞典").ConfigureAwait(false);
      return chk?.Contains("OK") ?? false;
    }

    public static string GetEztransDirFromReg() {
      RegistryKey key = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
      return key.OpenSubKey(@"Software\ChangShin\ezTrans").GetValue(@"FilePath") as string;
    }

    private async Task LoadNativeDll(string eztPath, int msDelay) {
      EzTransDll = LoadLibrary(GetDllPath(eztPath));
      if (EzTransDll == IntPtr.Zero) {
        int errorCode = Marshal.GetLastWin32Error();
        throw new Exception($"라이브러리 로드 실패(에러 코드: {errorCode})");
      }
      IntPtr addr = GetProcAddress(EzTransDll, "J2K_TranslateMMNTW");
      if (addr == IntPtr.Zero) {
        throw new Exception($"Ehnd 파일이 아닙니다.");
      }
      J2kMmntw = Marshal.GetDelegateForFunctionPointer<J2K_TranslateMMNTW>(addr);
      addr = GetProcAddress(EzTransDll, "J2K_FreeMem");
      J2kFree = Marshal.GetDelegateForFunctionPointer<J2K_FreeMem>(addr);
      addr = GetProcAddress(EzTransDll, "J2K_InitializeEx");
      var initEx = Marshal.GetDelegateForFunctionPointer<J2K_InitializeEx>(addr);
      await Task.Delay(msDelay).ConfigureAwait(false);
      string key = Path.Combine(eztPath, "Dat");
      if (!initEx("CSUSER123455", key)) {
        throw new Exception("엔진 초기화에 실패했습니다.");
      }
    }

    private string TranslateInternal(string jpStr) {
      var sb = new StringBuilder();
      var escaper = new EzTransEscaper();
      string e = escaper.Escape(jpStr, sb);
      IntPtr p = J2kMmntw(0, e);
      if (p == IntPtr.Zero) {
        return null;
      }
      string ret = Marshal.PtrToStringAuto(p);
      J2kFree(p);
      string ue = ret == null ? null : escaper.Unescape(ret, sb);
      return ue;
    }

    public async Task<string> Translate(string jpStr) {
      await InitDll.ConfigureAwait(false);
      return TranslateInternal(jpStr);
    }

    public void Dispose() {
      Dispose(true);
    }

    protected void Dispose(bool disposing) {
      // 원래 FreeLibrary를 호출하려 했는데 그러면 Access violation이 뜬다.
    }

    #region PInvoke
    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string libname);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    delegate bool J2K_InitializeEx(
      [MarshalAs(UnmanagedType.LPStr)] string user,
      [MarshalAs(UnmanagedType.LPStr)] string key);
    delegate IntPtr J2K_TranslateMMNTW(int data0, [MarshalAs(UnmanagedType.LPWStr)] string jpStr);
    delegate void J2K_FreeMem(IntPtr ptr);
    #endregion
  }


  /// <summary>
  /// 이지트랜스가 중복/줄 끝 공백 등을 제거하기 때문에 보존하려면 전/후처리 과정이 필요.
  /// </summary>
  internal class EzTransEscaper {

    private static readonly EncodingTester Sjis = new EncodingTester(932);
    private static readonly Regex RxDecode =
      new Regex(@"~x([0-9A-F]{4})>|~X([0-9A-F]{4})([0-9A-F]{3})>|.[^~]*", RegexOptions.Compiled);

    /// <summary>
    /// 연속된 상태로 꿀도르를 통과했을 때 변형 가능성이 있는 문자를 거름.
    /// </summary>
    private static bool IsSequenceMutableChar(char c) {
      return c == '─' || c == '―' || c == '#' || c == '\\';
    }

    private static bool IsSpaceExceptNewline(char c) {
      return c != '\r' && c != '\n' && char.IsWhiteSpace(c);
    }

    private int Count;
    private char Space = '\x1234';
    /// <summary>
    /// trim되어 날아가기 전에 보존할 맨 처음 부분 \n의 갯수
    /// </summary>
    private int LeadingLFCount;

    public string Escape(string notEscaped, StringBuilder buffer) {

      LeadingLFCount = notEscaped.TakeWhile(c => c == '\n').Count();

      buffer.Clear();
      buffer.EnsureCapacity(notEscaped.Length * 2);
      var white = new EzTransEscaper();
      foreach (char c in notEscaped.Skip(LeadingLFCount)) {
        if (white.IsEscaped(c, buffer)) {
          continue;
        }
        // @은 꿀도르 이스케이프. 가끔가다 사라짐.
        // -은 소스코드 이스케이프. ―로 바뀌거나 함.
        // \은 중복되면 생략될 때가 있음.
        else if (c == '@' || c == '-' || !Sjis.IsEncodable(c)) {
          buffer.AppendFormat("~x{0:X4}>", (int)c);
        }
        else {
          buffer.Append(c);
        }
      }
      white.FinishEscaping(buffer);

      return buffer.ToString();
    }

    public string Unescape(string escaped, StringBuilder buffer) {
      buffer.Clear();
      buffer.EnsureCapacity(escaped.Length + LeadingLFCount);

      buffer.Append(new string('\n', LeadingLFCount));

      foreach (Match m in RxDecode.Matches(escaped)) {
        if (m.Groups[1].Success) {
          buffer.Append((char)Convert.ToInt32(m.Groups[1].Value, 16));
        }
        else if (m.Groups[2].Success) {
          char space = (char)Convert.ToInt32(m.Groups[2].Value, 16);
          int cnt = Convert.ToInt32(m.Groups[3].Value, 16);
          buffer.Append(new string(space, cnt));
        }
        else {
          buffer.Append(m.Value);
        }
      }
      return buffer.ToString();
    }

    private bool IsEscaped(char c, StringBuilder buffer) {
      if (c == Space) {
        Count++;
        return true;
      }
      FlushEscapedWhitespace(c, buffer);
      if (IsSequenceMutableChar(c) || IsSpaceExceptNewline(c)) {
        Space = c;
        Count = 1;
        return true;
      }
      return false;
    }

    /// <summary>
    /// 보존 중인 공백을 전부 기록
    /// </summary>
    /// <param name="c">현재 처리 중인 문자</param>
    private void FlushEscapedWhitespace(char c, StringBuilder buffer) {
      if (Count > 1) {
        WriteDuplicateSpaceEscapeTo(buffer);
      }
      else if (Count > 0) {
        if (c == '\r' || c == '\n') {
          WriteTrailingSpaceEscapeTo(buffer);
        }
        else {
          buffer.Append(Space);
        }
      }
      Count = 0;
    }

    private void WriteTrailingSpaceEscapeTo(StringBuilder buffer) {
      buffer.AppendFormat("~x{0:X4}>", (int)Space);
    }

    private void WriteDuplicateSpaceEscapeTo(StringBuilder buffer) {
      buffer.AppendFormat("~X{0:X4}{1:X3}>", (int)Space, Count);
    }

    /// <summary>
    /// 보존 중인 공백을 전부 기록. 마지막에 사용.
    /// </summary>
    private void FinishEscaping(StringBuilder buffer) {
      FlushEscapedWhitespace('\r', buffer);
      FlushEscapedWhitespace('\n', buffer);
    }
  }
}
