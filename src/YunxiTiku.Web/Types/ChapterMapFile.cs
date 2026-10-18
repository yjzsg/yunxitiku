using System.Data;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Collections.Concurrent;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Data.Sqlite;
using Puller = JkdWeb.BankPuller;
using static AppHelpers;

/// <summary>外挂映射文件的内容。只含章名表和 subjectId → 章索引，不含任何题干。</summary>
sealed class ChapterMapFile
{
    public int Version { get; set; } = 1;
    public string GeneratedAt { get; set; } = "";
    /// <summary>生成时的题库签名。与当前签名不一致说明题库换过，映射过期。</summary>
    public string BankSignature { get; set; } = "";
    public List<string> Chapters { get; set; } = new();
    /// <summary>subjectId → 课本章在 Chapters 里的下标。一题多章时数组长度 &gt; 1。</summary>
    public Dictionary<string, int[]> Map { get; set; } = new(StringComparer.Ordinal);
    public int MappedQuestions { get; set; }
    public int MultiChapterQuestions { get; set; }
}
