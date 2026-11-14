namespace KimiTranslate
{
    internal class Program
    {

        static async Task Main(string[] args)
        {
            // 配置信息
            // 密钥从环境变量读取，不要把真实 Key 写进源码
            string apiKey = Environment.GetEnvironmentVariable("AI_API_KEY") ?? string.Empty;
            string apiUrl = "https://ai.goodfly.space/v1/chat/completions";
            string model = "gpt-6-sol"; // 选择合适的模型
            //string inputFilePath = "D:\\Games\\Steam\\steamapps\\workshop\\content\\268500\\1731235406\\Localization\\XComGame.int"; // 输入文件路径

            string outputFilePath = string.Empty;
            FileInfo curFile = null;
            //已传入文件直接开始
            if (args.Length > 0)
            {
                var file = args[0];
                if (File.Exists(file) && file.EndsWith(".int"))
                {
                    curFile = new FileInfo(file);
                    outputFilePath = file.Replace(".int", ".chn");
                }
            }
            else
            {
                var path = string.Empty;
                while (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                {
                    Console.WriteLine("请输入文件夹路径：");
                    path = Console.ReadLine();
                }
                Console.WriteLine("当前文件夹存在以下语言文件：");
                var files = Directory.GetFiles(path, "*.int", SearchOption.AllDirectories);
                List<FileInfo> filesList = new List<FileInfo>();
                for (int i = 0; i < files.Length; i++)
                {
                    var fileinfo = new FileInfo(files[i]);
                    filesList.Add(fileinfo);
                    Console.WriteLine($"{i + 1}、{fileinfo.Name}");
                }
                int index = 0;
                while (index <= 0 || index > files.Length)
                {
                    index = 0;
                    Console.WriteLine("请选择需要翻译的文件序号：");
                    var req = Console.ReadLine();
                    int.TryParse(req, out index);
                }

                curFile = filesList[index - 1];

                Console.WriteLine($"当前处理文件为：{curFile.Name}");
                outputFilePath = files[index - 1].Replace(".int", ".chn"); // 输出文件路径

                Console.WriteLine("请输入翻译后的文件名,不输入则自动按后缀：");
                var outputFileName = Console.ReadLine();
                if (!string.IsNullOrEmpty(outputFileName))
                {
                    outputFilePath = Path.Combine(path, outputFileName);
                }
            }



            // 使用XCOMTranslater进行翻译
            using (var translater = new XCOMTranslater(apiKey, apiUrl, model))
            {
                await translater.TranslateFileAsync(curFile.FullName, outputFilePath);
            }
            Console.ReadLine();
        }
    }
}
