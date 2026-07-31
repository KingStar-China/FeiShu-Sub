using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace FeishuMinutes
{
    public sealed class MinutesDownloader
    {
        private const string UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

        private static readonly Regex ShareUrlPattern = new Regex(
            @"^https?://[^\s]+/minutes/(?<token>[A-Za-z0-9]+)(?:[/?#]|$)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex BootDataPattern = new Regex(
            @"window\.bootDataReady\(""(?<payload>(?:\\.|[^""\\])*)""\)",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex SpeakerPattern = new Regex(
            @"^(.*?)\s+(\d{2}):(\d{2}):(\d{2})\.(\d{3})\s*$",
            RegexOptions.Compiled);

        private readonly JavaScriptSerializer _json = new JavaScriptSerializer
        {
            MaxJsonLength = int.MaxValue,
            RecursionLimit = 512
        };

        public static bool TryParseShareUrl(string value, out Uri uri, out string token)
        {
            uri = null;
            token = null;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            Match match = ShareUrlPattern.Match(value.Trim());
            if (!match.Success || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out uri))
            {
                return false;
            }

            token = match.Groups["token"].Value;
            return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp;
        }

        public async Task<DownloadResult> DownloadAsync(
            DownloadRequest request,
            CancellationToken cancellationToken,
            IProgress<DownloadProgress> progress = null)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (!TryParseShareUrl(request.ShareUrl, out Uri shareUri, out string token))
            {
                throw new ArgumentException("链接格式不正确，需要包含 /minutes/<token>。", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(request.OutputRoot))
            {
                throw new ArgumentException("保存位置不能为空。", nameof(request));
            }

            Report(progress, 1, "正在建立匿名会话", "访问妙记分享页，不需要飞书登录", "[*] 正在访问妙记分享页...");

            CookieContainer cookies = new CookieContainer();
            using (HttpClientHandler handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 20,
                UseCookies = true,
                CookieContainer = cookies,
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            })
            using (HttpClient client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromSeconds(90);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);

                string pageHtml;
                Uri finalPageUri;
                using (HttpResponseMessage pageResponse = await client.GetAsync(shareUri, cancellationToken).ConfigureAwait(false))
                {
                    pageHtml = await pageResponse.Content.ReadAsStringAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!pageResponse.IsSuccessStatusCode)
                    {
                        throw CreateHttpException("妙记分享页", pageResponse.StatusCode, pageHtml);
                    }

                    finalPageUri = pageResponse.RequestMessage.RequestUri ?? shareUri;
                }

                string title = ExtractTopic(pageHtml);
                if (!string.IsNullOrWhiteSpace(title))
                {
                    Report(progress, 1, "已读取妙记信息", "正在请求逐句字幕数据", "[*] 妙记标题: " + title);
                }
                else
                {
                    Report(progress, 1, "已建立匿名会话", "未读到标题，将使用默认文件名", "[!] 未从分享页读取到标题，使用 subtitles_word");
                }

                string authority = finalPageUri.GetLeftPart(UriPartial.Authority);
                string apiRoot = authority.TrimEnd('/') + "/minutes/api";
                Uri referer = shareUri;

                string paragraphUrl = apiRoot +
                    "/subtitles/paragraph-ids?page_size=10000&page_num=0" +
                    "&object_token=" + Uri.EscapeDataString(token) +
                    "&language=zh_cn";

                string paragraphJson = await GetApiStringAsync(
                    client,
                    new Uri(paragraphUrl),
                    referer,
                    "段落列表",
                    cancellationToken).ConfigureAwait(false);

                Dictionary<string, object> paragraphRoot = ParseApiRoot(paragraphJson, "段落列表");
                List<ParagraphId> paragraphIds = ParseParagraphIds(paragraphRoot);
                if (paragraphIds.Count == 0)
                {
                    throw new InvalidOperationException("妙记没有返回可用的字幕段落。请确认分享链接仍然有效且允许访问。");
                }

                Report(
                    progress,
                    2,
                    "正在提取字幕",
                    "已找到 " + paragraphIds.Count.ToString(CultureInfo.InvariantCulture) + " 个段落",
                    "[*] 段落数: " + paragraphIds.Count.ToString(CultureInfo.InvariantCulture));

                string probeUrl = apiRoot +
                    "/subtitles_v2?size=500&translate_lang=default" +
                    "&is_fluent=false&filter_speaker=true" +
                    "&object_token=" + Uri.EscapeDataString(token) +
                    "&language=zh_cn";

                string probeJson = await GetApiStringAsync(
                    client,
                    new Uri(probeUrl),
                    referer,
                    "逐句字幕",
                    cancellationToken).ConfigureAwait(false);

                Dictionary<string, object> probeRoot = ParseApiRoot(probeJson, "逐句字幕");
                var probeResult = ParseWords(probeRoot);
                List<TimedWord> words;

                if (probeResult.ParagraphStarts.Count >= Math.Max(2, paragraphIds.Count / 2))
                {
                    words = Deduplicate(probeResult.Words);
                    Report(
                        progress,
                        2,
                        "正在整理时间轴",
                        "接口一次返回全部字幕",
                        "[*] 一次返回全量 " + probeResult.ParagraphStarts.Count + "/" + paragraphIds.Count + " 个段落");
                }
                else
                {
                    words = new List<TimedWord>();
                    for (int index = 0; index < paragraphIds.Count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        ParagraphId paragraph = paragraphIds[index];
                        string itemUrl = apiRoot +
                            "/subtitles_v2?paragraph_id=" + Uri.EscapeDataString(paragraph.Id) +
                            "&size=500&translate_lang=default&is_fluent=false&filter_speaker=true" +
                            "&object_token=" + Uri.EscapeDataString(token) +
                            "&language=zh_cn";

                        string itemJson = await GetApiStringAsync(
                            client,
                            new Uri(itemUrl),
                            referer,
                            "字幕段落 " + (index + 1),
                            cancellationToken).ConfigureAwait(false);

                        words.AddRange(ParseWords(ParseApiRoot(itemJson, "字幕段落")).Words);
                        if ((index + 1) % 25 == 0 || index + 1 == paragraphIds.Count)
                        {
                            Report(
                                progress,
                                2,
                                "正在提取字幕",
                                "进度 " + (index + 1) + "/" + paragraphIds.Count,
                                "[*] 进度 " + (index + 1) + "/" + paragraphIds.Count);
                        }
                    }

                    words = Deduplicate(words);
                }

                if (words.Count == 0)
                {
                    throw new InvalidOperationException("接口返回成功，但没有解析到任何逐句字幕内容。");
                }

                List<SubtitleGroup> groups = BuildGroups(
                    words,
                    request.MaxCharacters,
                    request.MaxDurationMilliseconds);

                string outputDirectory = Path.GetFullPath(request.OutputRoot);
                Directory.CreateDirectory(outputDirectory);
                string baseName = request.NameByTitle && !string.IsNullOrWhiteSpace(title)
                    ? SafeFileName(title)
                    : "subtitles_word";
                string subtitlePath = Path.Combine(outputDirectory, baseName + ".srt");
                WriteSrt(subtitlePath, groups, null);

                string speakerSubtitlePath = null;
                string transcriptPath = Path.Combine(outputDirectory, "transcript.txt");
                List<SpeakerSegment> speakers = LoadSpeakers(transcriptPath);
                if (speakers.Count > 0)
                {
                    speakerSubtitlePath = Path.Combine(outputDirectory, baseName + "_speaker.srt");
                    WriteSrt(speakerSubtitlePath, groups, speakers);
                }

                Report(
                    progress,
                    3,
                    "字幕下载完成",
                    groups.Count + " 行 SRT 已保存到本地",
                    "[+] 已生成: " + subtitlePath);

                if (speakerSubtitlePath != null)
                {
                    Report(progress, 3, "字幕下载完成", "同时生成了说话人版本", "[+] 已生成: " + speakerSubtitlePath);
                }

                return new DownloadResult
                {
                    Token = token,
                    Title = title,
                    OutputDirectory = outputDirectory,
                    SubtitlePath = subtitlePath,
                    SpeakerSubtitlePath = speakerSubtitlePath,
                    ParagraphCount = paragraphIds.Count,
                    WordCount = words.Count,
                    SubtitleLineCount = groups.Count
                };
            }
        }

        private async Task<string> GetApiStringAsync(
            HttpClient client,
            Uri uri,
            Uri referer,
            string label,
            CancellationToken cancellationToken)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri))
            {
                request.Headers.Referrer = referer;
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/plain"));
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

                using (HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false))
                {
                    string body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!response.IsSuccessStatusCode)
                    {
                        throw CreateHttpException(label, response.StatusCode, body);
                    }

                    return body;
                }
            }
        }

        private Dictionary<string, object> ParseApiRoot(string json, string label)
        {
            Dictionary<string, object> root;
            try
            {
                root = AsDictionary(_json.DeserializeObject(json));
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(label + "返回了无法解析的数据。", exception);
            }

            long code = GetLong(root, "code", 0);
            if (code != 0)
            {
                string message = GetString(root, "msg") ?? "未知接口错误";
                throw new InvalidOperationException(label + "失败: " + message + " (code " + code + ")");
            }

            return root;
        }

        private static List<ParagraphId> ParseParagraphIds(Dictionary<string, object> root)
        {
            var result = new List<ParagraphId>();
            Dictionary<string, object> data = GetDictionary(root, "data");
            foreach (object itemObject in GetItems(data, "list"))
            {
                Dictionary<string, object> item = AsDictionary(itemObject);
                string id = GetString(item, "pid");
                if (!string.IsNullOrWhiteSpace(id))
                {
                    result.Add(new ParagraphId
                    {
                        Id = id,
                        StartTime = GetLong(item, "start_time", 0)
                    });
                }
            }

            return result;
        }

        private static (List<TimedWord> Words, HashSet<long> ParagraphStarts) ParseWords(
            Dictionary<string, object> root)
        {
            var words = new List<TimedWord>();
            var paragraphStarts = new HashSet<long>();
            Dictionary<string, object> data = GetDictionary(root, "data");
            var paragraphs = new List<object>(GetItems(data, "paragraphs"));

            if (paragraphs.Count == 0)
            {
                foreach (object itemObject in GetItems(data, "items"))
                {
                    paragraphs.AddRange(GetItems(AsDictionary(itemObject), "paragraphs"));
                }
            }

            int sequence = 0;
            foreach (object paragraphObject in paragraphs)
            {
                Dictionary<string, object> paragraph = AsDictionary(paragraphObject);
                long paragraphStart = GetLong(paragraph, "start_time", long.MinValue);
                if (paragraphStart != long.MinValue)
                {
                    paragraphStarts.Add(paragraphStart);
                }

                foreach (object sentenceObject in GetItems(paragraph, "sentences"))
                {
                    Dictionary<string, object> sentence = AsDictionary(sentenceObject);
                    foreach (object contentObject in GetItems(sentence, "contents"))
                    {
                        Dictionary<string, object> content = AsDictionary(contentObject);
                        string language = GetString(content, "language");
                        if (!string.IsNullOrEmpty(language) &&
                            !string.Equals(language, "zh_cn", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        string text = (GetString(content, "content") ?? string.Empty).Trim();
                        if (text.Length == 0)
                        {
                            continue;
                        }

                        long start = GetLong(content, "start_time", 0);
                        long stop = GetLong(content, "stop_time", 0);
                        if (stop == 0)
                        {
                            stop = start + 300;
                        }

                        words.Add(new TimedWord
                        {
                            StartTime = start,
                            StopTime = stop,
                            Text = text,
                            Sequence = sequence++
                        });
                    }
                }
            }

            return (words, paragraphStarts);
        }

        private static List<TimedWord> Deduplicate(IEnumerable<TimedWord> source)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var result = new List<TimedWord>();
            foreach (TimedWord word in source.OrderBy(item => item.StartTime).ThenBy(item => item.Sequence))
            {
                string key = word.StartTime.ToString(CultureInfo.InvariantCulture) + "\0" + word.Text;
                if (seen.Add(key))
                {
                    result.Add(word);
                }
            }

            return result;
        }

        private static List<SubtitleGroup> BuildGroups(
            IEnumerable<TimedWord> words,
            int maxCharacters,
            int maxDurationMilliseconds)
        {
            var groups = new List<SubtitleGroup>();
            SubtitleGroup current = null;
            int currentLength = 0;

            foreach (TimedWord word in words)
            {
                int wordLength = CountTextElements(word.Text);
                if (current == null)
                {
                    current = new SubtitleGroup
                    {
                        StartTime = word.StartTime,
                        StopTime = word.StopTime
                    };
                    current.Words.Add(word.Text);
                    currentLength = wordLength;
                    continue;
                }

                long newDuration = word.StopTime - current.StartTime;
                if (newDuration > maxDurationMilliseconds || currentLength + wordLength > maxCharacters)
                {
                    groups.Add(current);
                    current = new SubtitleGroup
                    {
                        StartTime = word.StartTime,
                        StopTime = word.StopTime
                    };
                    current.Words.Add(word.Text);
                    currentLength = wordLength;
                }
                else
                {
                    current.StopTime = word.StopTime;
                    current.Words.Add(word.Text);
                    currentLength += wordLength;
                }
            }

            if (current != null)
            {
                groups.Add(current);
            }

            return groups;
        }

        private static void WriteSrt(
            string path,
            IReadOnlyList<SubtitleGroup> groups,
            IReadOnlyList<SpeakerSegment> speakers)
        {
            var builder = new StringBuilder(groups.Count * 80);
            for (int index = 0; index < groups.Count; index++)
            {
                SubtitleGroup group = groups[index];
                string speakerPrefix = string.Empty;
                if (speakers != null && speakers.Count > 0)
                {
                    SpeakerSegment nearest = speakers
                        .OrderBy(segment => Math.Abs(segment.TimeMilliseconds - group.StartTime))
                        .First();
                    speakerPrefix = nearest.Speaker + "：";
                }

                builder.Append(index + 1).Append("\r\n");
                builder.Append(FormatTime(group.StartTime))
                    .Append(" --> ")
                    .Append(FormatTime(group.StopTime))
                    .Append("\r\n");
                builder.Append(speakerPrefix)
                    .Append(string.Concat(group.Words))
                    .Append("\r\n\r\n");
            }

            File.WriteAllText(path, builder.ToString(), new UTF8Encoding(false));
        }

        private static List<SpeakerSegment> LoadSpeakers(string transcriptPath)
        {
            var segments = new List<SpeakerSegment>();
            if (!File.Exists(transcriptPath))
            {
                return segments;
            }

            foreach (string line in File.ReadLines(transcriptPath, Encoding.UTF8))
            {
                Match match = SpeakerPattern.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                long hours = long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
                long minutes = long.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
                long seconds = long.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture);
                long milliseconds = long.Parse(match.Groups[5].Value, CultureInfo.InvariantCulture);
                segments.Add(new SpeakerSegment
                {
                    Speaker = match.Groups[1].Value.Trim(),
                    TimeMilliseconds = hours * 3600000 + minutes * 60000 + seconds * 1000 + milliseconds
                });
            }

            return segments;
        }

        private string ExtractTopic(string html)
        {
            if (string.IsNullOrEmpty(html))
            {
                return string.Empty;
            }

            Match match = BootDataPattern.Match(html);
            if (!match.Success)
            {
                return string.Empty;
            }

            try
            {
                string encodedPayload = match.Groups["payload"].Value;
                string bootJson = _json.Deserialize<string>("\"" + encodedPayload + "\"");
                Dictionary<string, object> root = AsDictionary(_json.DeserializeObject(bootJson));
                string topic = GetString(GetDictionary(root, "baseInfo"), "topic") ?? string.Empty;
                return Regex.Replace(topic, @"\s+", " ").Trim();
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string SafeFileName(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                builder.Append(invalid.Contains(character) || char.IsControl(character) ? '_' : character);
            }

            string result = Regex.Replace(builder.ToString(), @"\s+", " ").Trim().TrimEnd('.');
            if (result.Length > 80)
            {
                result = result.Substring(0, 80).TrimEnd();
                if (result.Length > 0 && char.IsHighSurrogate(result[result.Length - 1]))
                {
                    result = result.Substring(0, result.Length - 1);
                }
            }

            return string.IsNullOrWhiteSpace(result) ? "subtitles_word" : result;
        }

        private static string FormatTime(long milliseconds)
        {
            long value = Math.Max(0, milliseconds);
            long hours = value / 3600000;
            value %= 3600000;
            long minutes = value / 60000;
            value %= 60000;
            long seconds = value / 1000;
            long remainder = value % 1000;
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0:00}:{1:00}:{2:00},{3:000}",
                hours,
                minutes,
                seconds,
                remainder);
        }

        private static int CountTextElements(string value)
        {
            return string.IsNullOrEmpty(value)
                ? 0
                : StringInfo.ParseCombiningCharacters(value).Length;
        }

        private static Exception CreateHttpException(string label, HttpStatusCode statusCode, string body)
        {
            string summary = Regex.Replace(body ?? string.Empty, @"\s+", " ").Trim();
            if (summary.Length > 240)
            {
                summary = summary.Substring(0, 240);
            }

            return new InvalidOperationException(
                label + "请求失败: HTTP " + (int)statusCode +
                (summary.Length > 0 ? " — " + summary : string.Empty));
        }

        private static Dictionary<string, object> AsDictionary(object value)
        {
            return value as Dictionary<string, object> ?? new Dictionary<string, object>();
        }

        private static Dictionary<string, object> GetDictionary(Dictionary<string, object> source, string key)
        {
            return source != null && source.TryGetValue(key, out object value)
                ? AsDictionary(value)
                : new Dictionary<string, object>();
        }

        private static IEnumerable<object> GetItems(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null)
            {
                return Enumerable.Empty<object>();
            }

            if (value is object[] array)
            {
                return array;
            }

            if (value is ArrayList arrayList)
            {
                return arrayList.Cast<object>();
            }

            if (value is IEnumerable enumerable && !(value is string))
            {
                return enumerable.Cast<object>();
            }

            return Enumerable.Empty<object>();
        }

        private static string GetString(Dictionary<string, object> source, string key)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null)
            {
                return null;
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static long GetLong(Dictionary<string, object> source, string key, long fallback)
        {
            if (source == null || !source.TryGetValue(key, out object value) || value == null)
            {
                return fallback;
            }

            try
            {
                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
            catch (Exception)
            {
                return fallback;
            }
        }

        private static void Report(
            IProgress<DownloadProgress> progress,
            int step,
            string status,
            string detail,
            string logLine)
        {
            progress?.Report(new DownloadProgress(step, status, detail, logLine));
        }
    }
}
