using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;

namespace KimiTranslate
{
    // --- 核心翻译服务 (重构版) ---
    public class XmlTranslationService
    {
        private readonly Settings _settings;
        private readonly TranslationApiService _apiService;

        public XmlTranslationService(Settings settings, TranslationApiService apiService)
        {
            _settings = settings;
            _apiService = apiService;
        }

        /// <summary>
        /// 翻译XML文件，并可以选择性地使用参考文件来复用已有翻译。
        /// </summary>
        /// <param name="inputPath">需要翻译的XML文件路径。</param>
        /// <param name="outputPath">翻译后保存的XML文件路径。</param>
        /// <param name="referenceXmlPath">（可选）用于参考的已翻译XML文件路径。</param>
        public async Task TranslateXmlFileAsync(string inputPath, string outputPath, string? referenceXmlPath = null)
        {
            Console.WriteLine("正在加载并解析主XML文件...");
            XDocument doc = XDocument.Load(inputPath, LoadOptions.PreserveWhitespace);

            // 1. 预加载参考文件内容到字典中，以实现高效查找
            var referenceTranslations = LoadReferenceTranslations(referenceXmlPath);

            // 2. 使用XPath找到所有可能需要翻译的节点
            var allNodes = doc.XPathSelectElements(_settings.TranslatableNodeXPath).ToList();
            if (!allNodes.Any())
            {
                Console.WriteLine("警告：根据提供的XPath未找到任何可翻译的节点。");
                doc.Save(outputPath); // 即使没有节点，也保存一份，确保输出文件存在
                return;
            }
            Console.WriteLine($"共找到 {allNodes.Count} 个待处理节点。");

            // 3. 将节点分为两组：从参考文件更新 或 通过API翻译
            var nodesToUpdateFromReference = new List<XElement>();
            var nodesToTranslateViaApi = new List<XElement>();

            foreach (var node in allNodes)
            {
                string? contentUid = node.Attribute("contentuid")?.Value;
                if (contentUid != null && referenceTranslations.TryGetValue(contentUid, out var translatedText))
                {
                    // 在参考字典中找到匹配项
                    node.Value = translatedText; // 直接更新
                    nodesToUpdateFromReference.Add(node);
                }
                else
                {
                    // 未找到，需要通过API翻译
                    nodesToTranslateViaApi.Add(node);
                }
            }

            // 4. 处理复用的节点
            if (nodesToUpdateFromReference.Any())
            {
                Console.WriteLine($"\n--- 成功从参考文件复用了 {nodesToUpdateFromReference.Count} 个节点的翻译 ---");
                foreach (var node in nodesToUpdateFromReference)
                {
                    Console.WriteLine($"  [复用] contentuid: {node.Attribute("contentuid")?.Value} -> {node.Value.Substring(0, Math.Min(node.Value.Length, 50))}...");
                }
            }

            // 5. 处理需要通过API翻译的节点
            if (nodesToTranslateViaApi.Any())
            {
                Console.WriteLine($"\n--- 剩余 {nodesToTranslateViaApi.Count} 个节点需要通过AI进行翻译 ---");
                await ProcessNodesWithApiAsync(nodesToTranslateViaApi);
            }
            else
            {
                Console.WriteLine("\n所有节点均已从参考文件更新，无需调用AI翻译。");
            }


            // 6. 保存修改后的整个XML文件
            Console.WriteLine("\n正在保存翻译后的文件...");
            SaveDocument(doc, outputPath);
            Console.WriteLine($"翻译完成！文件已保存至: {outputPath}");
        }

        /// <summary>
        /// 从参考XML文件加载contentuid -> text的映射字典。
        /// </summary>
        private Dictionary<string, string> LoadReferenceTranslations(string? referenceXmlPath)
        {
            var dictionary = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(referenceXmlPath))
            {
                Console.WriteLine("未提供参考文件，将全部使用AI翻译。");
                return dictionary;
            }

            try
            {
                Console.WriteLine($"正在加载参考文件: {referenceXmlPath}");
                XDocument refDoc = XDocument.Load(referenceXmlPath);
                var refNodes = refDoc.XPathSelectElements(_settings.TranslatableNodeXPath);

                foreach (var node in refNodes)
                {
                    string? contentUid = node.Attribute("contentuid")?.Value;
                    if (contentUid != null && !dictionary.ContainsKey(contentUid))
                    {
                        dictionary.Add(contentUid, node.Value);
                    }
                }
                Console.WriteLine($"成功加载 {dictionary.Count} 个参考翻译条目。");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"警告：加载参考文件 '{referenceXmlPath}' 失败: {ex.Message}。将继续执行而不使用参考文件。");
            }

            return dictionary;
        }

        /// <summary>
        /// 使用API分批处理需要翻译的节点列表。
        /// </summary>
        private async Task ProcessNodesWithApiAsync(List<XElement> nodesToTranslate)
        {
            int totalBatches = (int)Math.Ceiling((double)nodesToTranslate.Count / _settings.BatchSize);
            Console.WriteLine($"将分为 {totalBatches} 批进行处理（每批最多 {_settings.BatchSize} 个节点）。");

            for (int i = 0; i < totalBatches; i++)
            {
                var currentBatch = nodesToTranslate
                    .Skip(i * _settings.BatchSize)
                    .Take(_settings.BatchSize)
                    .ToList();

                Console.WriteLine($"\n--- 正在处理第 {i + 1} / {totalBatches} 批 (AI翻译) ---");
                var batchWithIds = new Dictionary<string, string>();
                for (int j = 0; j < currentBatch.Count; j++)
                {
                    batchWithIds.Add($"id_{j}", currentBatch[j].Value);
                }

                var translatedBatch = await _apiService.TranslateBatchAsync(batchWithIds);

                if (translatedBatch != null)
                {
                    foreach (var item in translatedBatch)
                    {
                        if (int.TryParse(item.Key.Replace("id_", ""), out int itemIndex) && itemIndex < currentBatch.Count)
                        {
                            currentBatch[itemIndex].Value = item.Value;
                            Console.WriteLine($"  [OK] id_{itemIndex}: {item.Value.Substring(0, Math.Min(item.Value.Length, 50))}...");
                        }
                    }
                }
                else
                {
                    Console.WriteLine($"  [错误] 第 {i + 1} 批翻译失败，已跳过。");
                }

                if (i < totalBatches - 1)
                {
                    await Task.Delay(_settings.ApiDelayMs);
                }
            }
        }

        /// <summary>
        /// 将XDocument以无BOM的UTF-8格式保存到文件。
        /// </summary>
        private void SaveDocument(XDocument doc, string outputPath)
        {
            var settings = new XmlWriterSettings
            {
                Encoding = new UTF8Encoding(false), // UTF-8 without BOM
                Indent = true,
                OmitXmlDeclaration = doc.Declaration == null
            };
            using (var writer = XmlWriter.Create(outputPath, settings))
            {
                doc.Save(writer);
            }
        }
    }
}
