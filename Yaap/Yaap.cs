﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.Formatting;
using System.Threading;
using JetBrains.Annotations;
using System.Net.Http.Headers;

namespace Yaap
{
    static class YaapRegistry
    {
        static Thread _monitorThread;
        static readonly char[] _chars = new char[Console.WindowWidth * 2];
        static readonly object _consoleLock = new object();
        static readonly object _threadLock = new object();
        static readonly IDictionary<int, Yaap> _instances = new ConcurrentDictionary<int, Yaap>();
        static int _maxYaapPosition;
        static int _totalLinesAddedAfterYaaps;
        static int _isRunning;
        static bool _wasCursorHidden;
        internal static ThreadLocal<Stack<Yaap>> YaapStack =
            new ThreadLocal<Stack<Yaap>>(() => new Stack<Yaap>());

        static bool _vt100IsSupported;
        static bool _isConsoleRedirected;

        static YaapRegistry()
        {
            _isConsoleRedirected = DetectConsoleRedirection();

            if (_isConsoleRedirected)
                return;

            AppDomain.CurrentDomain.ProcessExit += (sender, args) => {
                OnCancelKeyPress(null, null);
            };

            if (Environment.OSVersion.Platform != PlatformID.Win32NT) {
                _vt100IsSupported = true;
                return;
            }

            _vt100IsSupported = RedPill();
            AppDomain.CurrentDomain.ProcessExit += (sender, args) => {
                BluePill();
            };
        }

        static bool DetectConsoleRedirection()
        {
            switch (Environment.OSVersion.Platform) {
                case PlatformID.Win32NT:
                    return Win32Console.DetectConsoleRedirectionOnWindows();

                case PlatformID.MacOSX:
                case PlatformID.Unix:
                    return DetectConsoleRedirectionOnPosix();
                default:
                    return false;
            }
        }

        static bool DetectConsoleRedirectionOnPosix() =>
            !Mono.Unix.Native.Syscall.isatty(1);

        static bool IsRunning
        {
            get => _isRunning == 1;
            set => Interlocked.Exchange(ref _isRunning, value ? 1 : 0);
        }

        internal static void AddInstance(Yaap yaap)
        {
            lock (_threadLock)
            {
                lock (_consoleLock)
                {
                    _instances.Add(GetOrSetVerticalPosition(yaap), yaap);

                    if (IsRunning) {
                        // Windows console (in the case we are on windows) has already been red-pilled
                        // So repaint() and byebye
                        RepaintSingleYaap(yaap);
                        return;
                    }

                    // If we are just starting up the monitoring thread, we've
                    // just potentially red-pilled the windows console, so we can repaint now
                    RepaintSingleYaap(yaap);

                    IsRunning = true;
                    Console.CancelKeyPress += OnCancelKeyPress;
                }

                if (_isConsoleRedirected)
                    return;
                _monitorThread = new Thread(UpdateYaaps) { Name = nameof(UpdateYaaps) };
                _monitorThread.Start();
            }
        }

        internal static void RemoveInstance(Yaap yaap)
        {
            lock (_threadLock)
            {
                lock (_consoleLock)
                {
                    // Repaint just before removing for cosmetic purposes:
                    // In case we didn't have a recent update to the progress bar, it might be @ 100%
                    // "in reality" but not visually.... This call will close that gap
                    if (yaap.Settings.Leave) {
                        RepaintSingleYaap(yaap);
                    }
                    else {
                        ClearSingleYaap(yaap);
                    }

                    _instances.Remove(yaap.Position);
                    // Unfortunately, we need to mark that we've drawn
                    // this Yaap for the last time while still holding the console lock...
                    yaap.IsDisposed = true;

                    if (_instances.Count > 0)
                        return;

                    IsRunning = false;
                    Console.CancelKeyPress -= OnCancelKeyPress;

                    if (yaap.Position + 1 == _maxYaapPosition)
                        _maxYaapPosition = _instances.Count == 0 ? 0 : (_instances.Keys.Max() + 1);

                    _totalLinesAddedAfterYaaps = 0;
                }
                _monitorThread.Join();
            }
        }

        static bool RedPill() => Win32Console.EnableVT100Stuffs();
        static void BluePill() => Win32Console.RestoreTerminalToPristineState();

        static int GetOrSetVerticalPosition(Yaap yaap)
        {
            switch (yaap.Settings.Positioning) {
                case YaapPositioning.FlowAndSnapToTop:
                    return FlowAndSnapToTop();
                case YaapPositioning.ClearAndAlignToTop:
                    return ClearAndAlignToTop();
                case YaapPositioning.FixToBottom:
                    return FixToBottom();
                default:
                    throw new ArgumentOutOfRangeException();
            }

            int FlowAndSnapToTop()
            {
                if (yaap.Settings.VerticalPosition.HasValue)
                    yaap.Position = yaap.Settings.VerticalPosition.Value;
                else {
                    var lastPos = -1;
                    foreach (var p in _instances.Keys) {
                        if (p > lastPos + 1)
                            return yaap.Position = lastPos + 1;
                        lastPos = p;
                    }

                    yaap.Position = ++lastPos;
                }

                if (_maxYaapPosition > yaap.Position)
                    return yaap.Position;

                // This progress bar is taking up one more line
                // than we previously accounted for, so bump the total line count + \n
                for (var l = _maxYaapPosition; l < yaap.Position + 1; l++)
                    Console.WriteLine();
                _maxYaapPosition = yaap.Position + 1;
                return yaap.Position;
            }

            int ClearAndAlignToTop()
            {
                ANSICodes.SetScrollableRegion(1, Console.WindowHeight);
                return yaap.Position = 0;
            }

            int FixToBottom()
            {
                ANSICodes.SetScrollableRegion(0, Console.WindowHeight - 1);
                return yaap.Position = Console.WindowHeight;
            }
        }

        static void OnCancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
            lock (_consoleLock) {
                Console.Write("\r");
                Console.Write(ANSICodes.EraseEntireLine);
                if (_wasCursorHidden) {
                    Console.CursorVisible = true;
                }
                ANSICodes.SetScrollableRegion(0, Console.BufferHeight+1);
            }
        }

        static void UpdateYaaps()
        {
            bool lockWasTaken = false;
            try {
                const int INTERVAL_MS = 50;
                _wasCursorHidden = false;
                while (IsRunning) {
                    foreach (var y in _instances.Values) {
                        if (!y.NeedsRepaint) {
                            continue;
                        }

                        if (!lockWasTaken)
                            Monitor.Enter(_consoleLock, ref lockWasTaken);

                        if (y.Settings.DisableCursorDuringUpdates && !_wasCursorHidden) {
                            Console.CursorVisible = false;
                            _wasCursorHidden = true;
                        }

                        RepaintSingleYaap(y);
                    }


                    if (_wasCursorHidden) {
                        Console.CursorVisible = true;
                        _wasCursorHidden = false;
                    }

                    if (lockWasTaken) {
                        lockWasTaken = false;
                        Monitor.Exit(_consoleLock);
                    }

                    Thread.Sleep(INTERVAL_MS);
                }
            }
            finally {
                if (lockWasTaken)
                    Monitor.Exit(_consoleLock);
            }
        }

        //static char[] _cursorOn  = {(char) 0x9B, (char) 0x3F, (char) 0x32, (char) 0x35, (char) 0x6C};
        //static char[] _cursorOff = {(char) 0x9B, (char) 0x3F, (char) 0x32, (char) 0x35, (char) 0x68};

        static void RepaintSingleYaap(Yaap yaap)
        {
            yaap.Buffer.Clear();
            (int x, int y) oldPos = (0,0);
            if (_vt100IsSupported) {
                MoveAndStoreOldPos(yaap);
            }
            else {
                oldPos = MoveTo(yaap);
            }


            var buffer = yaap.Repaint();
            // We may have been already disposed while awaiting to be repainted...
            // (Yes, this actually happenned...)
            if (yaap.IsDisposed) {
                return;
            }

            if (_vt100IsSupported) {
                RestorePos(yaap);
                buffer.CopyTo(0, _chars, 0, buffer.Count);
                Console.Write(_chars, 0, buffer.Count);

            } else {
                buffer.CopyTo(0, _chars, 0, buffer.Count);
                Console.Write(_chars, 0, buffer.Count);
                MoveTo(oldPos);
            }
        }

        static void RestorePos(Yaap yaap)
        {
            yaap.Buffer.Append(ANSICodes.RestoreCursorPosition);
        }

        static void MoveAndStoreOldPos(Yaap yaap)
        {
            yaap.Buffer.Append(ANSICodes.SaveCursorPosition);
            yaap.Buffer.AppendFormat(ANSICodes.CSI + "{0}d", yaap.Position);
        }

        static void ClearSingleYaap(Yaap yaap)
        {
            lock (_consoleLock) {
                var oldPos = MoveTo(yaap);
                ClearEntireLine();
                MoveTo(oldPos);
            }

        }

        internal static void ClearScreen()
        {
            lock (_consoleLock) {
                Console.Write(ANSICodes.ClearScreen);
            }
        }

        internal static void ClearEntireLine()
        {
            lock (_consoleLock) {
                Console.Write(ANSICodes.EraseEntireLine);
            }
        }
        internal static void ClearLineToEnd()
        {
            lock (_consoleLock) {
                Console.Write(ANSICodes.EraseToLineEnd);
            }
        }

        internal static void ClearLineToStart()
        {
            lock (_consoleLock) {
                Console.Write(ANSICodes.EraseToLineStart);
            }
        }

        static void MoveTo((int x, int y) oldPos)
        {
            Console.CursorLeft = oldPos.x;
            Console.CursorTop = oldPos.y;
        }

        static (int x, int y) MoveTo(Yaap yaap)
        {
            var (x, y) = ConsolePosition;
            switch (yaap.Settings.Positioning) {
                case YaapPositioning.FlowAndSnapToTop:
                    Console.CursorTop = Math.Max(0, y - (_maxYaapPosition - yaap.Position + _totalLinesAddedAfterYaaps));
                    break;
                case YaapPositioning.ClearAndAlignToTop:
                case YaapPositioning.FixToBottom:
                    Console.CursorTop = yaap.Position;
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
            return (x, y);

        }

        static (int x, int y) ConsolePosition => (Console.CursorLeft, Console.CursorTop);

        internal static void Write(string s) { lock (_consoleLock) { Console.Write(s); } }

        internal static void WriteLine(string s) {
            lock (_consoleLock) {
                Console.WriteLine(s);
                if (_maxYaapPosition > 0)
                    _totalLinesAddedAfterYaaps++;
            } }

        internal static void WriteLine()
        {
            lock (_consoleLock) {
                Console.WriteLine();
                if (_maxYaapPosition > 0)
                    _totalLinesAddedAfterYaaps++;
            }
        }

    }

    /// <inheritdoc />
    /// <summary>
    /// Represents a text mode progress bar control, that can visually provide user feedback as to the progress
    /// a long-standing operation, including progress visualization, elapsed time, total time, rate and more
    /// </summary>
    public class Yaap : IDisposable
    {
        const double TOLERANCE = 1e-6;

        static bool _unicodeNotWorky;
        static readonly char[] _asciiBarStyle = { '#' };
        readonly char[] _selectedBarStyle;
        double _nextRepaintProgress;
        readonly string _progressCountFmt;
        readonly int _maxGlyphWidth;
        readonly double _repaintProgressIncrement;
        internal readonly Stopwatch _sw;
        TimeSpan _totalTime;
        static readonly long _swTicksIn1Hour = Stopwatch.Frequency * 3600;
        long _lastRepaintTicks;
        double _rate;
        long _lastProgress;
        readonly string _unitName;
        readonly string _description;
        readonly bool _useMetricAbbreviations;
        readonly double _smoothingFactor;
        internal StringBuffer Buffer { get; }

        static Yaap()
        {
            void DoUnspeakableThingsOnWindows()
            {
                // ReSharper disable once HeapView.ObjectAllocation.Evident
                var acceptableUnicodeFonts = new[] {
                    "Hack",
                    "InputMono",
                    "Hasklig",
                    "DejaVu Sans Mono",
                    "Iosevka",
                };

                var vt100IsGo = Win32Console.EnableVT100Stuffs();
                _unicodeNotWorky = vt100IsGo &&
                    acceptableUnicodeFonts.FirstOrDefault(s => Win32Console.ConsoleFontName.StartsWith(s, StringComparison.InvariantCulture)) == null;
            }

            if (Environment.OSVersion.Platform == PlatformID.Win32NT) {
                DoUnspeakableThingsOnWindows();
            }
            else {
                _unicodeNotWorky = !(Console.OutputEncoding is UTF8Encoding);
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="T:Yaap.Yaap"/> class.
        /// </summary>
        /// <param name="total">The (optional)total number of elements of the wrapped <see cref="IEnumerable{T}"/> that will be enumerated</param>
        /// <param name="initialProgress">The (optional) initial progress value</param>
        /// <param name="settings">The (optional) visual settings overrides</param>
        [PublicAPI]
        public Yaap(long total, long initialProgress = 0, YaapSettings settings = null)
        {
            int epilogueLen;
            if (total < 0) {
                throw new ArgumentOutOfRangeException(nameof(total), "cannot be negative");
            }

            if (initialProgress < 0) {
                throw new ArgumentOutOfRangeException(nameof(initialProgress), "cannot be negative");
            }

            Settings = settings ?? new YaapSettings();
            Total = total;
            Progress = initialProgress;

            _unitName = Settings.UnitName;
            _description = Settings.Description;
            _useMetricAbbreviations = Settings.MetricAbbreviations;
            _smoothingFactor = Settings.SmoothingFactor;

            Buffer = new StringBuffer(Console.WindowWidth);

            if (Settings.UseASCII || _unicodeNotWorky) {
                _selectedBarStyle = _asciiBarStyle;
            }
            else {
                _selectedBarStyle = YaapBarStyleDefs.Glyphs[(int) Settings.Style];
            }

            if (Settings.MetricAbbreviations) {
                var (abbrevTotal, suffix) = GetMetricAbbreviation(total);
                _progressCountFmt = $"{{0,3}}{{1}}/{abbrevTotal}{suffix}";
                epilogueLen = "|123K/999K".Length;
            } else {
                var totalChars = CountDigits(Total);
                _progressCountFmt = $"{{0,{totalChars}}}/{total}";
                epilogueLen = 1 + totalChars * 2 + 1;
            }

            //_timeFmt = $"[{{0}}<{{1}}, {{2}}{unitName}/s]";
            const string EPILOGUE_SAMPLE = " [11:22s<33:44s, 123.45/s]";

            epilogueLen += EPILOGUE_SAMPLE.Length + Settings.UnitName.Length;

            var capturedWidth = Console.WindowWidth - 2;
            if (Settings.Width.HasValue && Settings.Width.Value < capturedWidth) {
                capturedWidth = Settings.Width.Value;
            }

            var prologueCount = (string.IsNullOrWhiteSpace(Settings.Description) ? 0 : Settings.Description.Length) + 7;

            _maxGlyphWidth = capturedWidth - prologueCount - epilogueLen;

            _repaintProgressIncrement = (double) Total / (_maxGlyphWidth * _selectedBarStyle.Length);
            if (Math.Abs(_repaintProgressIncrement) < TOLERANCE) {
                _repaintProgressIncrement = 1;
            }

            _nextRepaintProgress =
                Progress / _repaintProgressIncrement * _repaintProgressIncrement +
                _repaintProgressIncrement;

            _rate = double.NaN;
            _totalTime = TimeSpan.MaxValue;
            _sw = Stopwatch.StartNew();

            Parent = YaapRegistry.YaapStack.Value.Count == 0
                ? null : YaapRegistry.YaapStack.Value.Peek();

            YaapRegistry.AddInstance(this);

            if (Parent != null) {
                Parent.Child = this;
            }

            YaapRegistry.YaapStack.Value.Push(this);
        }


        Yaap Parent { get; set; }

        Yaap Child { get; set; }

        /// <summary>
        /// The current progress value of the progress bar
        /// <remarks>Always between 0 .. <see cref="Total"/></remarks>
        /// </summary>
        [PublicAPI]
        public long Progress { get; set; }

        double NestedProgress =>
            Child == null ?
                0 :
                (Child.Progress + Child.NestedProgress) / Child.Total;

        /// <summary>
        /// The maximal value of the progress bar which represents 100%
        /// <remarks>When the value is not supplied, only basic statistics will be displayed</remarks>
        /// </summary>
        [PublicAPI]
        public long Total { get; }

        /// <summary>
        /// Whether to disable the entire progressbar display
        /// </summary>
        [PublicAPI]
        public bool Disable { get; set; }

        /// <summary>
        /// The vertical position of this instance in relation to other concurrently "live" <see cref="Yaap"/> objects
        /// </summary>
        [PublicAPI]
        public int Position { get; internal set; }

        /// <summary>
        /// The visual settings used for this instance
        /// </summary>
        /// <value>The settings.</value>
        [PublicAPI]
        public YaapSettings Settings { get; }

        /// <summary>
        /// The elapsed amount of time this operation has taken so far
        /// </summary>
        /// <value>The elapsed time.</value>
        [PublicAPI]
        public TimeSpan ElapsedTime { get; private set; }

        /// <summary>
        /// The predicted total amount of time this operation will take
        /// </summary>
        /// <value>The total time.</value>
        [PublicAPI]
        public TimeSpan TotalTime { get; private set; }


        int _forceRepaint;
        bool ForceRepaint
        {
            get => _forceRepaint == 1;
            set => Interlocked.Exchange(ref _forceRepaint, value ? 1 : 0);
        }


        /// <summary>
        /// The current <see cref="YaapState"/> of the instance
        /// </summary>
        /// <value>The state.</value>
        public YaapState State
        {
            get => _state;
            set {
                if (_state == value) {
                    return;
                }

                _state = value;
                ForceRepaint = true;
            }
        }

        static readonly string[] _metricUnits = { "", "k", "M", "G", "T", "P", "E", "Z" };
        TerminalColor _lastColor;
        YaapState _state;

        static (long num, string abbrev) GetMetricAbbreviation(long num)
        {
            for (var i = 0; i < _metricUnits.Length; i++) {
                if (num < 1000) {
                    return (num, _metricUnits[i]);
                }

                num /= 1000;
            }
            throw new ArgumentOutOfRangeException(nameof(num), "is too large");
        }

        static (double num, string abbrev) GetMetricAbbreviation(double num)
        {
            // ReSharper disable once ForCanBeConvertedToForeach
            for (var i = 0; i < _metricUnits.Length; i++) {
                if (num < 1000) {
                    return (num, _metricUnits[i]);
                }

                num /= 1000;
            }

            throw new ArgumentOutOfRangeException(nameof(num), "is too large");
        }

        static int CountDigits(long number)
        {
            var digits = 0;
            while (number != 0) {
                number /= 10;
                digits++;
            }
            return digits;
        }

        internal bool NeedsRepaint
        {
            get
            {
                var updateSpan = Stopwatch.Frequency;
                var swElapsedTicks = _sw.ElapsedTicks;

                if (swElapsedTicks >= _swTicksIn1Hour || _totalTime.Ticks >= TimeSpan.TicksPerHour)
                    updateSpan = Stopwatch.Frequency * 60;

                if (ForceRepaint) {
                    ForceRepaint = false;
                    return true;
                }

                if (_lastRepaintTicks + updateSpan < swElapsedTicks)
                    return true;

                if ((Progress + NestedProgress)>= _nextRepaintProgress) {
                    _nextRepaintProgress += _repaintProgressIncrement;
                    return true;
                }

                return false;
            }
        }

        int _isDisposed;

        internal bool IsDisposed
        {
            get => _isDisposed == 1;
            set => Interlocked.Exchange(ref _isDisposed, value ? 1 : 0);
        }

        /// <summary>
        /// Releases all resources used by the progress bar
        /// </summary>
        public void Dispose()
        {
            YaapRegistry.YaapStack.Value.Pop();
            YaapRegistry.RemoveInstance(this);
        }


        bool ShouldShoveDescription     => (Settings.Elements & YaapElement.Description) != 0;
        bool ShouldShoveProgressPercent => (Settings.Elements & YaapElement.ProgressPercent) != 0;
        bool ShouldShoveProgressBar     => (Settings.Elements & YaapElement.ProgressBar) != 0;
        bool ShouldShoveProgressCount   => (Settings.Elements & YaapElement.ProgressCount) != 0;
        bool ShouldShoveTime            => (Settings.Elements & YaapElement.Time) != 0;
        bool ShouldShoveRate            => (Settings.Elements & YaapElement.Rate) != 0;

        internal StringBuffer Repaint()
        {
            // Capture progress while repainting
            var progress = Progress;
            var nestedProgress = NestedProgress;
            var elapsedTicks = _sw.ElapsedTicks;

            (_rate, _totalTime) = RecalculateRateAndTotalTime();

            var cs = Settings.ColorScheme;

            //Buffer.Clear();
            Buffer.Append('\r');
            Buffer.Append(ANSICodes.EraseToLineEnd);

            if (ShouldShoveDescription) {
                ShoveDescription();
            }

            if (ShouldShoveProgressPercent) {
                ShoveProgressPercentage();
            }

            if (ShouldShoveProgressBar) {
                ShoveProgressBar();
            }

            if (ShouldShoveProgressCount) {
                ShoveProgressTotals();
            }

            Buffer.Append(' ');
            //[{{0}}<{{1}}, {{2}}{unitName}/s]

            // At least one of Time|Rate is turned on?
            if ((Settings.Elements & (YaapElement.Rate | YaapElement.Time)) != 0) {
                Buffer.Append('[');
            }

            ShoveTime();
            if (ShouldShoveTime) {
                Buffer.Append(", ");
            }

            if (ShouldShoveRate) {
                ShoveRate();
            }

            if ((Settings.Elements & (YaapElement.Rate | YaapElement.Time)) != 0) {
                Buffer.Append(']');
            }

            Buffer.Append(ANSICodes.EraseToLineEnd);

            _lastProgress = progress;
            _lastRepaintTicks = elapsedTicks;
            return Buffer;

            (double rate, TimeSpan totalTime) RecalculateRateAndTotalTime()
            {
                // If we're "told" not to smooth out the rate/total time prediciton,
                // we just use the whole thing for the progress calc, otherwise we continuously sample
                // the last rate update since the previous rate and smooth it out using EMA/SmoothingFactor
                double rate;
                if (Math.Abs(_smoothingFactor) < TOLERANCE) {
                    rate = ((double) progress * Stopwatch.Frequency) / elapsedTicks;
                }
                else {
                    var dProgress = progress - _lastProgress;
                    var dTicks = elapsedTicks - _lastRepaintTicks;

                    var lastRate = ((double) dProgress * Stopwatch.Frequency) / dTicks;
                    rate = _lastRepaintTicks == 0 ? lastRate : _smoothingFactor * lastRate + (1 - _smoothingFactor) * _rate;
                }

                var totalTime = rate <= 0 ? TimeSpan.MaxValue : new TimeSpan((long) (Total * TimeSpan.TicksPerSecond / rate));
                // In case rate is so slow, we are overflowing
                if (totalTime.Ticks < 0)
                    totalTime = TimeSpan.MaxValue;
                return (rate, totalTime);
            }


            void ShoveDescription()
            {
                if (string.IsNullOrWhiteSpace(_description)) {
                    return;
                }
                Buffer.Append(_description);
                Buffer.Append(": ");
            }

            void ShoveProgressPercentage()
            {
                ChangeColor(cs.ProgressPercentColor);
                Buffer.AppendFormat("{0,3}%", (int) (((progress + nestedProgress) / Total) * 100));
                ResetColor();
            }

            void ShoveProgressBar()
            {
                Buffer.Append('|');

                ChangeColor(SelectProgressBarColor());
                var numChars = _selectedBarStyle.Length > 1
                    ? RenderComplexProgressGlyphs()
                    : RenderSimpleProgressGlyphs();
                ResetColor();
                var numSpaces = _maxGlyphWidth - numChars;
                if (numSpaces > 0)
                    Buffer.Append(' ', numSpaces);
                Buffer.Append('|');

                TerminalColor SelectProgressBarColor()
                {
                    switch (State) {
                        case YaapState.Running: return Settings.ColorScheme.ProgressBarColor;
                        case YaapState.Paused:  return Settings.ColorScheme.ProgressBarPausedColor;
                        case YaapState.Stalled: return Settings.ColorScheme.ProgressBarStalledColor;

                    }
                    throw new ArgumentOutOfRangeException();
                }

                int RenderSimpleProgressGlyphs()
                {
                    var numGlypchChars = (int)((progress * _maxGlyphWidth) / Total);
                    Buffer.Append(_selectedBarStyle[0], numGlypchChars);
                    return numGlypchChars;
                }

                int RenderComplexProgressGlyphs()
                {
                    var blocks = ((progress + nestedProgress) * (_maxGlyphWidth * _selectedBarStyle.Length)) / Total;
                    Debug.Assert(blocks >= 0);
                    var completeBlocks = (int)(blocks / _selectedBarStyle.Length);
                    Buffer.Append(_selectedBarStyle[_selectedBarStyle.Length - 1], completeBlocks);
                    var lastCharIdx = (int)(blocks % _selectedBarStyle.Length);

                    if (lastCharIdx == 0) {
                        return completeBlocks;
                    }

                    Buffer.Append(_selectedBarStyle[lastCharIdx]);
                    return completeBlocks + 1;
                }

            }

            void ShoveProgressTotals()
            {
                ChangeColor(cs.ProgressCountColor);
                if (_useMetricAbbreviations) {
                    var (abbrevNum, suffix) = GetMetricAbbreviation(Progress);
                    Buffer.AppendFormat(_progressCountFmt, abbrevNum, suffix);
                }
                else {
                    Buffer.AppendFormat(_progressCountFmt, Progress);
                }

                ResetColor();
            }


            void ShoveTime()
            {
                ChangeColor(cs.TimeColor);
                WriteTimes(Buffer, new TimeSpan((elapsedTicks * TimeSpan.TicksPerSecond) / Stopwatch.Frequency), _totalTime);
                ResetColor();
            }

            void ShoveRate()
            {
                ChangeColor(cs.RateColor);
                if (_useMetricAbbreviations) {
                    var (abbrevNum, suffix) = GetMetricAbbreviation(_rate);
                    if (abbrevNum < 100)
                        Buffer.AppendFormat("{0:F2}{1}{2}/s", abbrevNum, suffix, _unitName);
                    else
                        Buffer.AppendFormat("{0}{1}{2}/s", (int) abbrevNum, suffix, _unitName);
                }
                else
                {
                    if (_rate < 100)
                        Buffer.AppendFormat("{0:F2}{1}/s", _rate, _unitName);
                    else
                        Buffer.AppendFormat("{0}{1}/s", (int) _rate, _unitName);
                }
                ResetColor();
            }
        }

        void ChangeColor(TerminalColor color)
        {
            _lastColor = color;
            Buffer.Append(color.EscapeCode);
        }

        void ResetColor()
        {
            if (_lastColor == TerminalColor.None)
                return;
            Buffer.Append(TerminalColor.Reset.EscapeCode);
        }

        static void WriteTimes(StringBuffer buffer, TimeSpan elapsed, TimeSpan remaining)
        {
            Debug.Assert(elapsed.Ticks >= 0);
            Debug.Assert(remaining.Ticks >= 0);
            var (edays, ehours, eminutes, eseconds, _) = elapsed;
            var (rdays, rhours, rminutes, rseconds, _) = remaining;

            if (edays + rdays > 0)
            {
                // Print days formatting
            }
            else if (ehours + rhours > 0) {
                if (elapsed == TimeSpan.MaxValue)
                    buffer.Append("--:--?<");
                else
                    buffer.AppendFormat("{0:D2}:{1:D2}m<", ehours, eminutes);
                if (remaining == TimeSpan.MaxValue)
                    buffer.Append("--:--?");
                else
                    buffer.AppendFormat("{0:D2}:{1:D2}m", rhours, rminutes);
            }
            else {
                if (elapsed == TimeSpan.MaxValue) {
                    buffer.Append("--:--?<");
                }
                else {
                    buffer.AppendFormat("{0:D2}:{1:D2}s<", eminutes, eseconds);
                }

                if (remaining == TimeSpan.MaxValue) {
                    buffer.Append("--:--?");
                }
                else {
                    buffer.AppendFormat("{0:D2}:{1:D2}s", rminutes, rseconds);
                }
            }
        }
    }

    /// <summary>
    /// Represents a Yaap wrapped <see cref="IEnumerable{T}"/> object, where the enumerator progress automatically changes
    /// the Yaap visual representation without further need to manually update the progress state
    /// </summary>
    /// <typeparam name="T">The type of objects to enumerate</typeparam>
    public class YaapEnumerable<T> : Yaap, IEnumerable<T>
    {
        readonly IEnumerable<T> _enumerable;
        static Func<IEnumerable<T>, int> _cheapCount;

        internal YaapEnumerable(IEnumerable<T> e, long total = -1, long initialProgress = 0, YaapSettings settings = null) :
            base(total != -1 ? total : GetCheapCount(e), initialProgress, settings)
        {
            _enumerable = e;
        }

        /// <summary>
        /// Attempt to get a "cheap" count value for the <see cref="IEnumerable{T}"/>, where "cheap" means that the enumerable is
        /// never consumed no matter what
        /// </summary>
        /// <param name="source">The <see cref="IEnumerable{T}"/> object</param>
        /// <returns>The count value, or -1 in case the cheap count failed</returns>
        public static int GetCheapCount(IEnumerable<T> source)
        {
            if (source == null)
                throw new ArgumentNullException(nameof(source));

            switch (source)
            {
                case ICollection<T> collectionoft:
                    return collectionoft.Count;
                case ICollection collection:
                    return collection.Count;
            }

            return CheapCountDelegate(source);

        }

        #region Avert Your Eyes!
        static Func<IEnumerable<T>, int> CheapCountDelegate
        {
            get
            {
                return _cheapCount ?? (_cheapCount = GenerateGetCount());

                Func<IEnumerable<T>, int> GenerateGetCount()
                {
                    var iilp = typeof(Enumerable).Assembly.GetType("System.Linq.IIListProvider`1");
                    Debug.Assert(iilp != null);
                    var iilpt = iilp.MakeGenericType(typeof(T));
                    Debug.Assert(iilpt != null);
                    var getCountMI = iilpt.GetMethod("GetCount", BindingFlags.Public | BindingFlags.Instance, null,
                        new[] { typeof(bool) }, null);
                    Debug.Assert(getCountMI != null);
                    var param = Expression.Parameter(typeof(IEnumerable<T>));

                    var castAndCall = Expression.Call(Expression.Convert(param, iilpt), getCountMI,
                        Expression.Constant(true));

                    var body = Expression.Condition(Expression.TypeIs(param, iilpt), castAndCall,
                        Expression.Constant(-1));

                    return Expression.Lambda<Func<IEnumerable<T>, int>>(body, new[] { param }).Compile();
                }
            }
        }
        #endregion

        /// <summary>
        /// Returns an enumerator that iterates through the collection.
        /// </summary>
        /// <returns>Returns an enumerator that iterates through the collection.</returns>
        public IEnumerator<T> GetEnumerator()
        {
            // In the case of enumerable we can actually start ticking the elapsed clock
            // at a later, more precise, time, so lets do it...
            _sw.Restart();
            foreach (var t in _enumerable)
            {
                yield return t;
                Progress++;
            }
            Dispose();
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// A static extension class that provides <see cref="IEnumerable{T}"/> <see cref="YaapEnumerable{T}"/> wrappers
    /// </summary>
    public static class YaapEnumerableExtensions
    {
        /// <summary>
        /// Wrap the provided <see cref="IEnumerable{T}"/> with a <see cref="YaapEnumerable{T}"/> object
        /// </summary>
        /// <typeparam name="T">The type of objects to enumerate</typeparam>
        /// <param name="e">The <see cref="IEnumerable{T}"/> instance to wrap</param>
        /// <param name="total">The (optional)total number of elements of the wrapped <see cref="IEnumerable{T}"/> that will be enumerated</param>
        /// <param name="initialProgress">The (optional) initial progress value</param>
        /// <param name="settings">The (optional) visual settings overrides</param>
        /// <returns>The newly instantiated <see cref="YaapEnumerable{T}"/> wrapping the provided <see cref="IEnumerable{T}"/></returns>
        public static YaapEnumerable<T> Yaap<T>(this IEnumerable<T> e, long total = -1, long initialProgress = 0, YaapSettings settings = null) =>
        new YaapEnumerable<T>(e, total, initialProgress, settings);
    }

    static class DateTimeDeconstruction
    {
        const long TicksPerMicroSeconds = 10;
        const long TicksPerMillisecond = 10_000;
        const long TicksPerSecond = TicksPerMillisecond * 1_000;
        const long TicksPerMinute = TicksPerSecond * 60;
        const long TicksPerHour = TicksPerMinute * 60;
        const long TicksPerDay = TicksPerHour * 24;

        public static void Deconstruct(this TimeSpan timeSpan, out int days, out int hours, out int minutes, out int seconds, out int ticks)
        {
            if (timeSpan == TimeSpan.MaxValue) {
                days = hours = minutes = seconds = ticks = 0;
                return;
            }
            var t = timeSpan.Ticks;
            days = (int)(t / (TicksPerHour * 24));
            hours = (int)((t / TicksPerHour) % 24);
            minutes = (int)((t / TicksPerMinute) % 60);
            seconds = (int)((t / TicksPerSecond) % 60);
            ticks = (int)(t % 10_000_000);
        }
    }
}
