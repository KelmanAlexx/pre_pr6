using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace SmartCampus.SilentUpgrade
{
    internal class Program
    {
        private static async Task<int> Main(string[] args)
        {
            string scenario = GetArgument(args, "--scenario", "success");
            bool reset = HasArgument(args, "--reset");
            bool offline = HasArgument(args, "--offline");

            if (scenario != "success" && scenario != "rollback")
            {
                Console.WriteLine("Использование: SmartCampusSilentUpgrade --scenario success|rollback [--reset] [--offline]");
                return 1;
            }

            string testFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SmartCampusSilentUpgradeDemo",
                scenario);

            // Сброс разрешен только по явному параметру --reset.
            if (reset && Directory.Exists(testFolder))
            {
                Directory.Delete(testFolder, true);
            }

            Directory.CreateDirectory(testFolder);
            Logger logger = new Logger(Path.Combine(testFolder, "logs", "upgrade.log"));
            UpgradeService service = new UpgradeService(testFolder, logger, scenario, offline);

            // Обновление выполняется в фоновой задаче без диалоговых окон и вопросов пользователю.
            Task<int> backgroundJob = Task.Run(async delegate
            {
                return await service.ExecuteAsync();
            });

            return await backgroundJob;
        }

        private static string GetArgument(string[] args, string name, string defaultValue)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name)
                {
                    return args[i + 1];
                }
            }

            return defaultValue;
        }

        private static bool HasArgument(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == name)
                {
                    return true;
                }
            }

            return false;
        }
    }

    internal sealed class UpgradeService
    {
        private const string BaseVersion = "1.0.0";
        private const string TargetVersion = "1.1.0";
        private const string MetadataUrl = "https://jsonplaceholder.typicode.com/posts/1";
        private const string TelemetryUrl = "https://jsonplaceholder.typicode.com/posts";

        private readonly string _root;
        private readonly string _scenario;
        private readonly bool _offline;
        private readonly Logger _logger;

        private readonly string _installDirectory;
        private readonly string _currentDirectory;
        private readonly string _previousDirectory;
        private readonly string _dataDirectory;
        private readonly string _workDirectory;
        private readonly string _packageSourceDirectory;
        private readonly string _archivePath;
        private readonly string _stagingDirectory;
        private readonly string _backupPath;

        private bool _releaseSwitched;
        private string _packageHash;

        public UpgradeService(string root, Logger logger, string scenario, bool offline)
        {
            _root = root;
            _logger = logger;
            _scenario = scenario;
            _offline = offline;

            _installDirectory = Path.Combine(_root, "install");
            _currentDirectory = Path.Combine(_installDirectory, "current");
            _previousDirectory = Path.Combine(_root, "rollback", "previous");
            _dataDirectory = Path.Combine(_root, "data");
            _workDirectory = Path.Combine(_root, "work");
            _packageSourceDirectory = Path.Combine(_workDirectory, "package-source");
            _archivePath = Path.Combine(_workDirectory, "release-package.zip");
            _stagingDirectory = Path.Combine(_workDirectory, "staging");
            _backupPath = Path.Combine(_root, "backups", DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        }

        public async Task<int> ExecuteAsync()
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            string initialVersion = BaseVersion;
            string finalStatus = "FAILED";
            string errorMessage = "";
            bool backupVerified = false;
            bool rollbackCompleted = false;
            BackupInfo backupInfo = new BackupInfo();

            try
            {
                InitializeEnvironment();
                initialVersion = ReadVersion(_currentDirectory);
                _logger.Info("Начало тихого обновления клиента ИС «Умный кампус».");
                _logger.Info("АРМ: DEMO-OPERATOR-01; сценарий: " + _scenario + "; версия до обновления: " + initialVersion);
                _logger.Progress(5, "Проверка каталогов и локальной конфигурации");

                if (initialVersion == TargetVersion)
                {
                    throw new InvalidOperationException("Целевая версия уже установлена. Для повторного учебного теста используйте параметр --reset.");
                }

                ReleaseMetadata metadata = await GetReleaseMetadataAsync();
                _logger.Progress(20, "Получены сведения о тестовом релизе");

                CreatePackage(metadata);
                _logger.Progress(35, "Тестовый пакет создан и проверен по SHA-256");

                ExtractPackage();
                _logger.Progress(50, "Файлы пакета распакованы во временный каталог");

                backupInfo = CreateBackup();
                backupVerified = backupInfo.Verified;
                if (!backupVerified)
                {
                    throw new IOException("Контрольная проверка резервной копии не пройдена.");
                }
                _logger.Progress(65, "Резервная копия конфигурации, кэша и SQLite проверена");

                ActivateRelease();
                _releaseSwitched = true;
                _logger.Progress(80, "Новая версия переключена в активный каталог");

                bool sanityPassed = RunSanityTests(backupInfo);
                if (!sanityPassed)
                {
                    throw new InvalidOperationException("Sanity Test завершился ошибкой. Запускается автоматический откат.");
                }

                SavePreviousRelease();
                finalStatus = "SUCCESS";
                _logger.Progress(100, "Обновление завершено успешно");
                _logger.Info("Установлена версия " + ReadVersion(_currentDirectory) + ". Данные пользователя сохранены.");
            }
            catch (HttpRequestException exception)
            {
                errorMessage = "Ошибка сетевого запроса: " + exception.Message;
                _logger.Error(errorMessage);
                rollbackCompleted = TryRollback(backupInfo);
                finalStatus = rollbackCompleted ? "ROLLED_BACK" : "FAILED";
            }
            catch (TaskCanceledException exception)
            {
                errorMessage = "Превышено время ожидания сетевого запроса: " + exception.Message;
                _logger.Error(errorMessage);
                rollbackCompleted = TryRollback(backupInfo);
                finalStatus = rollbackCompleted ? "ROLLED_BACK" : "FAILED";
            }
            catch (IOException exception)
            {
                errorMessage = "Ошибка работы с файлами: " + exception.Message;
                _logger.Error(errorMessage);
                rollbackCompleted = TryRollback(backupInfo);
                finalStatus = rollbackCompleted ? "ROLLED_BACK" : "FAILED";
            }
            catch (UnauthorizedAccessException exception)
            {
                errorMessage = "Недостаточно прав для операции с файлами: " + exception.Message;
                _logger.Error(errorMessage);
                rollbackCompleted = TryRollback(backupInfo);
                finalStatus = rollbackCompleted ? "ROLLED_BACK" : "FAILED";
            }
            catch (Exception exception)
            {
                errorMessage = exception.Message;
                _logger.Error("Критическая ошибка: " + exception.GetType().Name + ": " + exception.Message);
                rollbackCompleted = TryRollback(backupInfo);
                finalStatus = rollbackCompleted ? "ROLLED_BACK" : "FAILED";
            }
            finally
            {
                stopwatch.Stop();
                string activeVersion = TryReadActiveVersion();
                await SendTelemetryAsync(finalStatus, initialVersion, activeVersion, stopwatch.Elapsed.TotalSeconds);
                WriteTestReport(finalStatus, initialVersion, activeVersion, backupVerified, rollbackCompleted, errorMessage, stopwatch.Elapsed.TotalSeconds);
            }

            if (finalStatus == "SUCCESS")
            {
                return 0;
            }
            if (finalStatus == "ROLLED_BACK")
            {
                return 3;
            }
            return 2;
        }

        private void InitializeEnvironment()
        {
            Directory.CreateDirectory(_root);
            Directory.CreateDirectory(_installDirectory);
            Directory.CreateDirectory(_dataDirectory);
            Directory.CreateDirectory(_workDirectory);
            Directory.CreateDirectory(Path.Combine(_root, "logs"));

            string sampleDirectory = Path.Combine(AppContext.BaseDirectory, "sample-data");
            string sampleConfig = Path.Combine(sampleDirectory, "appsettings.json");
            string sampleDatabase = Path.Combine(sampleDirectory, "campus.db");
            string sampleCache = Path.Combine(sampleDirectory, "cache");

            if (!File.Exists(Path.Combine(_dataDirectory, "appsettings.json")))
            {
                File.Copy(sampleConfig, Path.Combine(_dataDirectory, "appsettings.json"), true);
            }
            if (!File.Exists(Path.Combine(_dataDirectory, "campus.db")))
            {
                File.Copy(sampleDatabase, Path.Combine(_dataDirectory, "campus.db"), true);
            }
            if (!Directory.Exists(Path.Combine(_dataDirectory, "cache")))
            {
                CopyDirectory(sampleCache, Path.Combine(_dataDirectory, "cache"));
            }

            if (!Directory.Exists(_currentDirectory))
            {
                Directory.CreateDirectory(_currentDirectory);
                File.WriteAllText(Path.Combine(_currentDirectory, "version.txt"), BaseVersion, Encoding.UTF8);
                File.WriteAllText(Path.Combine(_currentDirectory, "client.txt"), "Учебный клиент АРМ оператора ИС «Умный кампус».", Encoding.UTF8);
            }
        }

        private async Task<ReleaseMetadata> GetReleaseMetadataAsync()
        {
            if (_offline)
            {
                _logger.Warning("Включен автономный режим. Используются встроенные тестовые метаданные.");
                return new ReleaseMetadata
                {
                    Id = 1,
                    UserId = 1,
                    Title = "Учебный релиз клиента Умного кампуса",
                    Body = "Демонстрационное описание обновления. Проверка интерфейса и исправление ошибок."
                };
            }

            using (HttpClient client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                HttpResponseMessage response = await client.GetAsync(MetadataUrl);
                response.EnsureSuccessStatusCode();
                string json = await response.Content.ReadAsStringAsync();
                ReleaseMetadata metadata = JsonSerializer.Deserialize<ReleaseMetadata>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (metadata == null || string.IsNullOrWhiteSpace(metadata.Title))
                {
                    throw new InvalidOperationException("Сервер вернул пустые или некорректные метаданные релиза.");
                }
                _logger.Info("Получен ответ JSONPlaceholder: HTTP " + (int)response.StatusCode + ", запись №" + metadata.Id + ".");
                return metadata;
            }
        }

        private void CreatePackage(ReleaseMetadata metadata)
        {
            if (Directory.Exists(_packageSourceDirectory))
            {
                Directory.Delete(_packageSourceDirectory, true);
            }
            if (File.Exists(_archivePath))
            {
                File.Delete(_archivePath);
            }

            Directory.CreateDirectory(_packageSourceDirectory);
            ReleaseInfo releaseInfo = new ReleaseInfo
            {
                Version = TargetVersion,
                MetadataId = metadata.Id,
                Title = metadata.Title,
                Description = metadata.Body,
                CreatedAt = DateTimeOffset.Now
            };

            File.WriteAllText(Path.Combine(_packageSourceDirectory, "version.txt"), TargetVersion, Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(_packageSourceDirectory, "release-info.json"),
                JsonSerializer.Serialize(releaseInfo, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(_packageSourceDirectory, "client.txt"),
                "Демонстрационная сборка клиента АРМ оператора. Версия " + TargetVersion,
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(_packageSourceDirectory, "release-note.txt"),
                "Обновление учебного модуля: тестовая сборка, проверка запуска и сохранности локальных данных.",
                Encoding.UTF8);

            ZipFile.CreateFromDirectory(_packageSourceDirectory, _archivePath, CompressionLevel.Optimal, false);
            _packageHash = ComputeSha256(_archivePath);
            File.WriteAllText(_archivePath + ".sha256", _packageHash, Encoding.ASCII);

            string storedHash = File.ReadAllText(_archivePath + ".sha256").Trim();
            string actualHash = ComputeSha256(_archivePath);
            if (!string.Equals(storedHash, actualHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Контрольная сумма ZIP-пакета не совпадает.");
            }

            _logger.Info("Архив создан: " + _archivePath);
            _logger.Info("SHA-256 пакета: " + _packageHash);
        }

        private void ExtractPackage()
        {
            if (Directory.Exists(_stagingDirectory))
            {
                Directory.Delete(_stagingDirectory, true);
            }

            ZipFile.ExtractToDirectory(_archivePath, _stagingDirectory);
            string extractedVersion = File.ReadAllText(Path.Combine(_stagingDirectory, "version.txt")).Trim();
            if (extractedVersion != TargetVersion)
            {
                throw new InvalidDataException("Версия в распакованном пакете не соответствует ожидаемой.");
            }
            if (!File.Exists(Path.Combine(_stagingDirectory, "release-info.json")))
            {
                throw new InvalidDataException("В пакете отсутствует release-info.json.");
            }
        }

        private BackupInfo CreateBackup()
        {
            _logger.Info("Остановка клиентского приложения перед копированием SQLite (в симуляторе имитируется).");
            Directory.CreateDirectory(_backupPath);
            Directory.CreateDirectory(Path.Combine(_backupPath, "cache"));

            BackupManifest manifest = new BackupManifest();
            BackupInfo result = new BackupInfo();
            result.BackupPath = _backupPath;

            AddBackupFile("appsettings.json", manifest);
            AddBackupFile("campus.db", manifest);
            CopyDirectory(Path.Combine(_dataDirectory, "cache"), Path.Combine(_backupPath, "cache"));
            AddCacheFiles(Path.Combine(_dataDirectory, "cache"), manifest);

            File.WriteAllText(
                Path.Combine(_backupPath, "backup-manifest.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }),
                Encoding.UTF8);

            result.DatabaseHash = ComputeSha256(Path.Combine(_backupPath, "campus.db"));
            result.Verified = VerifyBackup(manifest);
            _logger.Info("Резервная копия создана: " + _backupPath);
            _logger.Info("Файлов в резервной копии: " + manifest.Files.Count + "; проверка: " + (result.Verified ? "OK" : "ERROR"));
            return result;
        }

        private void AddBackupFile(string fileName, BackupManifest manifest)
        {
            string source = Path.Combine(_dataDirectory, fileName);
            string target = Path.Combine(_backupPath, fileName);
            File.Copy(source, target, true);
            manifest.Files.Add(new BackupFileInfo
            {
                RelativePath = fileName,
                Sha256 = ComputeSha256(source)
            });
        }

        private void AddCacheFiles(string sourceDirectory, BackupManifest manifest)
        {
            string[] files = Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories);
            for (int i = 0; i < files.Length; i++)
            {
                string relative = Path.GetRelativePath(_dataDirectory, files[i]);
                manifest.Files.Add(new BackupFileInfo
                {
                    RelativePath = relative,
                    Sha256 = ComputeSha256(files[i])
                });
            }
        }

        private bool VerifyBackup(BackupManifest manifest)
        {
            for (int i = 0; i < manifest.Files.Count; i++)
            {
                string relative = manifest.Files[i].RelativePath;
                string backupFile = Path.Combine(_backupPath, relative);
                if (!File.Exists(backupFile))
                {
                    return false;
                }
                if (!string.Equals(ComputeSha256(backupFile), manifest.Files[i].Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }
            return true;
        }

        private void ActivateRelease()
        {
            string rollbackFolder = Path.GetDirectoryName(_previousDirectory);
            Directory.CreateDirectory(rollbackFolder);

            if (Directory.Exists(_previousDirectory))
            {
                Directory.Delete(_previousDirectory, true);
            }

            Directory.Move(_currentDirectory, _previousDirectory);
            try
            {
                Directory.Move(_stagingDirectory, _currentDirectory);
            }
            catch
            {
                if (!Directory.Exists(_currentDirectory) && Directory.Exists(_previousDirectory))
                {
                    Directory.Move(_previousDirectory, _currentDirectory);
                }
                throw;
            }
        }

        private bool RunSanityTests(BackupInfo backupInfo)
        {
            _logger.Info("Выполняются Sanity Test: версия, конфигурация, локальная БД и кэш.");

            bool versionOk = ReadVersion(_currentDirectory) == TargetVersion;
            bool configOk = IsValidJson(Path.Combine(_dataDirectory, "appsettings.json"));
            bool databaseOk = File.Exists(Path.Combine(_dataDirectory, "campus.db"))
                && string.Equals(ComputeSha256(Path.Combine(_dataDirectory, "campus.db")), backupInfo.DatabaseHash, StringComparison.OrdinalIgnoreCase);
            bool cacheOk = Directory.Exists(Path.Combine(_dataDirectory, "cache"));
            bool packageOk = File.Exists(Path.Combine(_currentDirectory, "release-info.json"));

            _logger.Info("Проверка версии: " + (versionOk ? "OK" : "FAIL"));
            _logger.Info("Проверка appsettings.json: " + (configOk ? "OK" : "FAIL"));
            _logger.Info("Проверка SQLite по SHA-256: " + (databaseOk ? "OK" : "FAIL"));
            _logger.Info("Проверка каталога кэша: " + (cacheOk ? "OK" : "FAIL"));
            _logger.Info("Проверка файлов релиза: " + (packageOk ? "OK" : "FAIL"));

            // Сценарий rollback специально имитирует ошибку запуска нового клиента.
            if (_scenario == "rollback")
            {
                _logger.Error("ИМИТАЦИЯ: клиент не прошел проверку запуска после обновления.");
                return false;
            }

            return versionOk && configOk && databaseOk && cacheOk && packageOk;
        }

        private void SavePreviousRelease()
        {
            if (!Directory.Exists(_previousDirectory))
            {
                return;
            }

            string oldVersion = ReadVersion(_previousDirectory);
            string releasesDirectory = Path.Combine(_root, "releases");
            Directory.CreateDirectory(releasesDirectory);
            string savedOldRelease = Path.Combine(releasesDirectory, oldVersion);

            if (Directory.Exists(savedOldRelease))
            {
                Directory.Delete(savedOldRelease, true);
            }
            Directory.Move(_previousDirectory, savedOldRelease);
            _logger.Info("Предыдущая версия сохранена в каталоге releases/" + oldVersion + ".");
        }

        private bool TryRollback(BackupInfo backupInfo)
        {
            if (!_releaseSwitched && !Directory.Exists(_previousDirectory))
            {
                _logger.Warning("Откат не требуется: активная версия еще не переключалась.");
                return false;
            }

            try
            {
                _logger.Warning("Выполняется Rollback на предыдущую стабильную версию.");

                if (Directory.Exists(_currentDirectory))
                {
                    Directory.Delete(_currentDirectory, true);
                }
                if (Directory.Exists(_previousDirectory))
                {
                    Directory.Move(_previousDirectory, _currentDirectory);
                }

                if (backupInfo != null && !string.IsNullOrEmpty(backupInfo.BackupPath) && Directory.Exists(backupInfo.BackupPath))
                {
                    RestoreBackup(backupInfo.BackupPath);
                }

                string restoredVersion = ReadVersion(_currentDirectory);
                bool databaseRestored = backupInfo == null
                    || string.IsNullOrEmpty(backupInfo.DatabaseHash)
                    || string.Equals(ComputeSha256(Path.Combine(_dataDirectory, "campus.db")), backupInfo.DatabaseHash, StringComparison.OrdinalIgnoreCase);

                bool success = restoredVersion == BaseVersion && databaseRestored;
                _logger.Info("Версия после отката: " + restoredVersion + "; проверка данных: " + (databaseRestored ? "OK" : "FAIL"));
                _logger.Info("Результат Rollback: " + (success ? "УСПЕШНО" : "ОШИБКА"));
                return success;
            }
            catch (Exception exception)
            {
                _logger.Error("Не удалось выполнить полный откат: " + exception.Message);
                return false;
            }
        }

        private void RestoreBackup(string backupDirectory)
        {
            File.Copy(Path.Combine(backupDirectory, "appsettings.json"), Path.Combine(_dataDirectory, "appsettings.json"), true);
            File.Copy(Path.Combine(backupDirectory, "campus.db"), Path.Combine(_dataDirectory, "campus.db"), true);

            string targetCache = Path.Combine(_dataDirectory, "cache");
            if (Directory.Exists(targetCache))
            {
                Directory.Delete(targetCache, true);
            }
            CopyDirectory(Path.Combine(backupDirectory, "cache"), targetCache);
        }

        private async Task SendTelemetryAsync(string status, string oldVersion, string activeVersion, double seconds)
        {
            if (_offline)
            {
                _logger.Warning("Телеметрия пропущена: включен автономный режим.");
                return;
            }

            TelemetryData telemetry = new TelemetryData
            {
                UserId = 1,
                Title = "Smart Campus upgrade metric",
                Body = "scenario=" + _scenario + "; status=" + status + "; from=" + oldVersion + "; to=" + activeVersion + "; durationSeconds=" + seconds.ToString("F2"),
                Scenario = _scenario,
                Status = status,
                PreviousVersion = oldVersion,
                ActiveVersion = activeVersion,
                DurationSeconds = Math.Round(seconds, 2),
                SentAt = DateTimeOffset.Now
            };

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    client.Timeout = TimeSpan.FromSeconds(10);
                    string json = JsonSerializer.Serialize(telemetry);
                    using (StringContent content = new StringContent(json, Encoding.UTF8, "application/json"))
                    {
                        HttpResponseMessage response = await client.PostAsync(TelemetryUrl, content);
                        response.EnsureSuccessStatusCode();
                        _logger.Info("Метрики отправлены в тестовый API: HTTP " + (int)response.StatusCode + ".");
                        _logger.Warning("JSONPlaceholder имитирует прием POST и не хранит отправленную телеметрию постоянно.");
                    }
                }
            }
            catch (Exception exception)
            {
                // Недоступность телеметрии не отменяет уже установленный релиз.
                _logger.Warning("Не удалось отправить метрики: " + exception.Message);
            }
        }

        private void WriteTestReport(string status, string oldVersion, string activeVersion, bool backupVerified, bool rollbackCompleted, string error, double seconds)
        {
            TestReport report = new TestReport
            {
                GeneratedAt = DateTimeOffset.Now,
                Scenario = _scenario,
                Status = status,
                VersionBefore = oldVersion,
                VersionAfter = activeVersion,
                BackupVerified = backupVerified,
                RollbackCompleted = rollbackCompleted,
                DurationSeconds = Math.Round(seconds, 2),
                Error = error
            };

            try
            {
                File.WriteAllText(
                    Path.Combine(_root, "test-report.json"),
                    JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                    Encoding.UTF8);
                _logger.Info("Отчет о тестировании сохранен: " + Path.Combine(_root, "test-report.json"));
            }
            catch (Exception exception)
            {
                _logger.Error("Не удалось записать отчет: " + exception.Message);
            }
        }

        private string TryReadActiveVersion()
        {
            try
            {
                return ReadVersion(_currentDirectory);
            }
            catch
            {
                return "UNKNOWN";
            }
        }

        private static string ReadVersion(string directory)
        {
            string versionFile = Path.Combine(directory, "version.txt");
            if (!File.Exists(versionFile))
            {
                throw new FileNotFoundException("Не найден файл version.txt.", versionFile);
            }
            return File.ReadAllText(versionFile).Trim();
        }

        private static bool IsValidJson(string filePath)
        {
            try
            {
                using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(filePath)))
                {
                    return document.RootElement.ValueKind == JsonValueKind.Object;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string ComputeSha256(string filePath)
        {
            using (FileStream stream = File.OpenRead(filePath))
            {
                byte[] hash = SHA256.HashData(stream);
                return Convert.ToHexString(hash);
            }
        }

        private static void CopyDirectory(string sourceDirectory, string targetDirectory)
        {
            Directory.CreateDirectory(targetDirectory);
            string[] files = Directory.GetFiles(sourceDirectory);
            for (int i = 0; i < files.Length; i++)
            {
                string targetFile = Path.Combine(targetDirectory, Path.GetFileName(files[i]));
                File.Copy(files[i], targetFile, true);
            }

            string[] directories = Directory.GetDirectories(sourceDirectory);
            for (int i = 0; i < directories.Length; i++)
            {
                string targetSubDirectory = Path.Combine(targetDirectory, Path.GetFileName(directories[i]));
                CopyDirectory(directories[i], targetSubDirectory);
            }
        }
    }

    internal sealed class Logger
    {
        private readonly string _logPath;

        public Logger(string logPath)
        {
            _logPath = logPath;
        }

        public void Info(string message)
        {
            Write("INFO", message);
        }

        public void Warning(string message)
        {
            Write("WARN", message);
        }

        public void Error(string message)
        {
            Write("ERROR", message);
        }

        public void Progress(int percent, string message)
        {
            Write("" + percent.ToString("D3") + "%", message);
        }

        private void Write(string level, string message)
        {
            string line = "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] [" + level + "] " + message;
            Console.WriteLine(line);
            try
            {
                string directory = Path.GetDirectoryName(_logPath);
                Directory.CreateDirectory(directory);
                File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("[LOGGER] Не удалось записать файл журнала: " + exception.Message);
            }
        }
    }

    internal sealed class ReleaseMetadata
    {
        public int UserId { get; set; }
        public int Id { get; set; }
        public string Title { get; set; }
        public string Body { get; set; }
    }

    internal sealed class ReleaseInfo
    {
        public string Version { get; set; }
        public int MetadataId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
    }

    internal sealed class BackupManifest
    {
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
        public List<BackupFileInfo> Files { get; set; } = new List<BackupFileInfo>();
    }

    internal sealed class BackupFileInfo
    {
        public string RelativePath { get; set; }
        public string Sha256 { get; set; }
    }

    internal sealed class BackupInfo
    {
        public string BackupPath { get; set; } = "";
        public string DatabaseHash { get; set; } = "";
        public bool Verified { get; set; }
    }

    internal sealed class TelemetryData
    {
        public int UserId { get; set; }
        public string Title { get; set; }
        public string Body { get; set; }
        public string Scenario { get; set; }
        public string Status { get; set; }
        public string PreviousVersion { get; set; }
        public string ActiveVersion { get; set; }
        public double DurationSeconds { get; set; }
        public DateTimeOffset SentAt { get; set; }
    }

    internal sealed class TestReport
    {
        public DateTimeOffset GeneratedAt { get; set; }
        public string Scenario { get; set; }
        public string Status { get; set; }
        public string VersionBefore { get; set; }
        public string VersionAfter { get; set; }
        public bool BackupVerified { get; set; }
        public bool RollbackCompleted { get; set; }
        public double DurationSeconds { get; set; }
        public string Error { get; set; }
    }
}
