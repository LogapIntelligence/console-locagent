using Serilog;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace AI.Services
{
    public interface IFileSystemService
    {
        string WorkingDirectory { get; }
        void ChangeDirectory(string path);
        List<string> GetFileStructure();
        string ReadFile(string relativePath);
        void WriteFile(string relativePath, string content);
        void DeleteFile(string relativePath);
    }

    public class FileSystemService : IFileSystemService
    {
        private string _workingDirectory;
        private readonly ILogger _logger;

        // Enhanced list of directories and file patterns to ignore
        private readonly HashSet<string> _ignoreDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".vscode", ".idea",
            "bin", "obj", "packages",
            "node_modules", "bower_components",
            "wwwroot", "ClientApp",
            "TestResults", "artifacts",
            ".nuget", "publish",
            "logs" // Added logs directory to ignore
        };

        private readonly HashSet<string> _ignoreExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".dll", ".exe", ".pdb", ".cache",
            ".user", ".suo", ".lock", ".ide",
            ".min.js", ".min.css",
            ".nupkg", ".snupkg"
        };

        public FileSystemService()
        {
            _workingDirectory = Directory.GetCurrentDirectory();
            _logger = Log.ForContext<FileSystemService>();
            _logger.Information("FileSystemService initialized with working directory: {WorkingDirectory}", _workingDirectory);
        }

        public string WorkingDirectory => _workingDirectory;

        public void ChangeDirectory(string path)
        {
            _logger.Information("Attempting to change directory from {CurrentDir} to {NewPath}", _workingDirectory, path);

            var originalDirectory = _workingDirectory;

            if (Path.IsPathRooted(path))
            {
                _workingDirectory = path;
            }
            else
            {
                _workingDirectory = Path.GetFullPath(Path.Combine(_workingDirectory, path));
            }

            if (!Directory.Exists(_workingDirectory))
            {
                _logger.Error("Directory not found: {Directory}", _workingDirectory);
                _workingDirectory = originalDirectory; // Restore original
                throw new DirectoryNotFoundException($"Directory not found: {_workingDirectory}");
            }

            _logger.Information("Successfully changed directory to: {NewDirectory}", _workingDirectory);
        }

        public List<string> GetFileStructure()
        {
            _logger.Debug("Getting file structure for: {WorkingDirectory}", _workingDirectory);

            var files = new List<string>();
            GetFilesRecursive(_workingDirectory, files, _workingDirectory);

            _logger.Information("File structure retrieved: {FileCount} files found", files.Count);
            _logger.Verbose("Files found: {Files}", string.Join(", ", files.Take(20)));

            return files;
        }

        private void GetFilesRecursive(string path, List<string> files, string basePath)
        {
            try
            {
                var dirName = Path.GetFileName(path);
                _logger.Verbose("Scanning directory: {Path}", path);

                // Get all files in current directory
                var filesInDir = Directory.GetFiles(path);
                var filesAdded = 0;
                var filesSkipped = 0;

                foreach (var file in filesInDir)
                {
                    var fileName = Path.GetFileName(file);
                    var extension = Path.GetExtension(file);

                    // Skip files with ignored extensions
                    if (_ignoreExtensions.Contains(extension))
                    {
                        filesSkipped++;
                        _logger.Verbose("Skipping file with ignored extension: {File}", fileName);
                        continue;
                    }

                    // Skip generated files
                    if (fileName.EndsWith(".g.cs") || fileName.EndsWith(".designer.cs"))
                    {
                        filesSkipped++;
                        _logger.Verbose("Skipping generated file: {File}", fileName);
                        continue;
                    }

                    var relativePath = Path.GetRelativePath(basePath, file);
                    files.Add(relativePath);
                    filesAdded++;
                }

                if (filesAdded > 0 || filesSkipped > 0)
                {
                    _logger.Debug("Directory {Dir}: Added {Added} files, Skipped {Skipped} files",
                        dirName, filesAdded, filesSkipped);
                }

                // Recursively process subdirectories
                var subdirs = Directory.GetDirectories(path);
                foreach (var dir in subdirs)
                {
                    var subDirName = Path.GetFileName(dir);

                    // Skip ignored directories
                    if (_ignoreDirs.Contains(subDirName))
                    {
                        _logger.Debug("Skipping ignored directory: {Dir}", subDirName);
                        continue;
                    }

                    GetFilesRecursive(dir, files, basePath);
                }
            }
            catch (UnauthorizedAccessException ex)
            {
                _logger.Warning("Access denied to directory {Path}: {Message}", path, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Error scanning directory {Path}", path);
            }
        }

        public string ReadFile(string relativePath)
        {
            var fullPath = Path.Combine(_workingDirectory, relativePath);
            _logger.Debug("Attempting to read file: {RelativePath} (Full: {FullPath})", relativePath, fullPath);

            if (!File.Exists(fullPath))
            {
                _logger.Warning("File not found: {FullPath}", fullPath);
                return null;
            }

            try
            {
                var content = File.ReadAllText(fullPath);
                _logger.Information("Successfully read file: {RelativePath} ({ByteCount} bytes)",
                    relativePath, content.Length);
                _logger.Verbose("File content preview (first 200 chars): {Content}",
                    content.Substring(0, Math.Min(200, content.Length)));
                return content;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to read file: {FullPath}", fullPath);
                return null;
            }
        }

        public void WriteFile(string relativePath, string content)
        {
            _logger.Information("Attempting to write file: {RelativePath} ({ByteCount} bytes)",
                relativePath, content?.Length ?? 0);

            try
            {
                ValidateFilePath(relativePath);

                var fullPath = Path.Combine(_workingDirectory, relativePath);
                _logger.Debug("Full path for write: {FullPath}", fullPath);

                if (Directory.Exists(fullPath))
                {
                    _logger.Error("Path is a directory, not a file: {FullPath}", fullPath);
                    throw new InvalidOperationException($"Path '{fullPath}' is a directory, not a file.");
                }

                var directory = Path.GetDirectoryName(fullPath);
                if (!Directory.Exists(directory))
                {
                    _logger.Information("Creating directory: {Directory}", directory);
                    Directory.CreateDirectory(directory);
                }

                var originalContent = content;
                content = CleanCodeFromMarkdown(content);

                if (originalContent.Length != content.Length)
                {
                    _logger.Debug("Cleaned markdown from content. Original: {Original} bytes, Cleaned: {Cleaned} bytes",
                        originalContent.Length, content.Length);
                }

                _logger.Information("Writing {ByteCount} bytes to: {RelativePath}", content.Length, relativePath);
                _logger.Verbose("Content preview (first 500 chars): {Content}",
                    content.Substring(0, Math.Min(500, content.Length)));

                Console.WriteLine($"  Writing to: {relativePath}");
                File.WriteAllText(fullPath, content);

                _logger.Information("Successfully wrote file: {RelativePath}", relativePath);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "Failed to write file: {RelativePath}", relativePath);
                throw;
            }
        }

        public void DeleteFile(string relativePath)
        {
            var fullPath = Path.Combine(_workingDirectory, relativePath);
            _logger.Information("Attempting to delete file: {RelativePath} (Full: {FullPath})",
                relativePath, fullPath);

            if (File.Exists(fullPath))
            {
                try
                {
                    File.Delete(fullPath);
                    _logger.Information("Successfully deleted file: {RelativePath}", relativePath);
                }
                catch (Exception ex)
                {
                    _logger.Error(ex, "Failed to delete file: {FullPath}", fullPath);
                    throw;
                }
            }
            else
            {
                _logger.Warning("File does not exist for deletion: {FullPath}", fullPath);
            }
        }

        private void ValidateFilePath(string relativePath)
        {
            _logger.Verbose("Validating file path: {Path}", relativePath);

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                _logger.Error("File path validation failed: Path is empty");
                throw new ArgumentException("File path cannot be empty");
            }

            var fileName = Path.GetFileName(relativePath);
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
            {
                _logger.Error("File path validation failed: Invalid filename in path {Path}", relativePath);
                throw new InvalidOperationException($"Invalid file path '{relativePath}'. Must include a filename with extension.");
            }

            _logger.Verbose("File path validation successful: {Path}", relativePath);
        }

        private string CleanCodeFromMarkdown(string content)
        {
            if (string.IsNullOrEmpty(content))
                return content;

            // Remove markdown code block markers
            var pattern = @"^```[a-zA-Z]*\r?\n|^```\s*$";
            var cleaned = Regex.Replace(content, pattern, "", RegexOptions.Multiline);

            if (cleaned.Length != content.Length)
            {
                _logger.Debug("Removed {CharCount} characters of markdown code block markers",
                    content.Length - cleaned.Length);
            }

            return cleaned;
        }
    }
}