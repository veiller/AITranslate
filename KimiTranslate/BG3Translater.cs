using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace KimiTranslate
{
    internal class BG3Translater
    {
        public static async Task Exec(string[] args)
        {
            // --- 1. 配置 ---
            // 将配置信息集中管理，避免硬编码
            var settings = new Settings
            {
                // 密钥从环境变量读取，不要把真实 Key 写进源码
                ApiKey = Environment.GetEnvironmentVariable("AI_API_KEY") ?? string.Empty,
                ApiUrl = "https://yunwu.ai/v1/chat/completions",
                Model = "gpt-4o-mini", // 选择合适的模型
                BatchSize = 150, // 每次API调用翻译的节点数量
                ApiDelayMs = 2000, // 每次API调用后的延迟
                                   // 关键：使用XPath定位需要翻译的节点。这个例子是翻译所有<content>标签的文本。
                                   // 你需要根据你的XML结构修改这个XPath。例如 "//LocalizedString" 或 "//Entry/Text"
                TranslatableNodeXPath = "//content"
            };
            // --- 2. 文件选择 ---
            string inputFilePath = GetInputFilePath(args);
            if (string.IsNullOrEmpty(inputFilePath))
            {
                Console.WriteLine("未选择文件，程序退出。");
                return;
            }
            string outputFilePath = inputFilePath.Replace(".xml", ".chs.xml"); // 智能生成输出文件名
            Console.WriteLine($"源文件: {inputFilePath}");
            Console.WriteLine($"目标文件: {outputFilePath}");
            string refFilePath = inputFilePath.Replace(".xml", ".ref.xml");
            if (File.Exists(refFilePath))
            {
                Console.WriteLine(value: $"参考文件: {refFilePath}");
            }

            Console.WriteLine("按 Enter 键开始翻译...");
            Console.ReadLine();
            // --- 3. 执行翻译 ---
            try
            {
                var apiService = new TranslationApiService(settings);
                var translationService = new XmlTranslationService(settings, apiService);
                await translationService.TranslateXmlFileAsync(inputFilePath, outputFilePath, (File.Exists(refFilePath) ? refFilePath : null));
                Console.WriteLine("\n翻译完成！结果已保存到文件中。");
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n发生严重错误: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
                Console.ResetColor();
            }
            Console.WriteLine("按 Enter 键退出。");
            Console.ReadLine();
        }
        private static string GetInputFilePath(string[] args)
        {
            // 简化文件选择逻辑
            if (args.Length > 0 && File.Exists(args[0]) && args[0].EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            {
                return args[0];
            }
            Console.WriteLine("请输入包含XML文件的文件夹路径：");
            string path = Console.ReadLine();
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                Console.WriteLine("路径无效。");
                return null;
            }
            var files = Directory.GetFiles(path, "*.xml", SearchOption.AllDirectories);
            if (files.Length == 0)
            {
                Console.WriteLine("未找到任何 .xml 文件。");
                return null;
            }
            Console.WriteLine("找到以下XML文件：");
            for (int i = 0; i < files.Length; i++)
            {
                Console.WriteLine($"{i + 1}. {Path.GetFileName(files[i])}");
            }
            Console.WriteLine("请选择需要翻译的文件序号：");
            if (int.TryParse(Console.ReadLine(), out int index) && index > 0 && index <= files.Length)
            {
                return files[index - 1];
            }
            Console.WriteLine("选择无效。");
            return null;
        }
    }
}
