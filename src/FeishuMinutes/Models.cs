using System;
using System.Collections.Generic;

namespace FeishuMinutes
{
    public sealed class DownloadRequest
    {
        public string ShareUrl { get; set; }
        public string OutputRoot { get; set; }
        public int MaxCharacters { get; set; } = 22;
        public int MaxDurationMilliseconds { get; set; } = 2500;
        public bool NameByTitle { get; set; } = true;
    }

    public sealed class DownloadResult
    {
        public string Token { get; set; }
        public string Title { get; set; }
        public string OutputDirectory { get; set; }
        public string SubtitlePath { get; set; }
        public string SpeakerSubtitlePath { get; set; }
        public int ParagraphCount { get; set; }
        public int WordCount { get; set; }
        public int SubtitleLineCount { get; set; }
    }

    public sealed class DownloadProgress
    {
        public DownloadProgress(int step, string status, string detail, string logLine)
        {
            Step = step;
            Status = status;
            Detail = detail;
            LogLine = logLine;
        }

        public int Step { get; }
        public string Status { get; }
        public string Detail { get; }
        public string LogLine { get; }
    }

    internal sealed class ParagraphId
    {
        public string Id { get; set; }
        public long StartTime { get; set; }
    }

    internal sealed class TimedWord
    {
        public long StartTime { get; set; }
        public long StopTime { get; set; }
        public string Text { get; set; }
        public int Sequence { get; set; }
    }

    internal sealed class SubtitleGroup
    {
        public long StartTime { get; set; }
        public long StopTime { get; set; }
        public List<string> Words { get; } = new List<string>();
    }

    internal sealed class SpeakerSegment
    {
        public string Speaker { get; set; }
        public long TimeMilliseconds { get; set; }
    }
}
