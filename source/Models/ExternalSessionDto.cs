using System;
using System.Collections.Generic;

namespace GameActivity.Models
{
    public class ExternalSessionDto
    {
        public string GameId { get; set; }
        public string SourceId { get; set; }
        public List<string> PlatformIds { get; set; } = new List<string>();
        public int IdConfiguration { get; set; } = -1;
        public string ConfigurationName { get; set; }
        public string GameActionName { get; set; }
        public DateTime DateSessionUtc { get; set; }
        public ulong ElapsedSeconds { get; set; }
    }

    public class ExternalImportResult
    {
        public int Applied { get; set; }
        public int Updated { get; set; }
        public int Skipped { get; set; }
        public int Errors { get; set; }
        public string Error { get; set; }
    }
}
