using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KimiTranslate
{
    // --- 配置类 ---
    public class Settings
    {
        public string ApiKey { get; set; }
        public string ApiUrl { get; set; }
        public string Model { get; set; }
        public int BatchSize { get; set; }
        public int ApiDelayMs { get; set; }
        public string TranslatableNodeXPath { get; set; }
    }
}
