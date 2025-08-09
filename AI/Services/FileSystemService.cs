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

        // Enhanced list of directories and file patterns to ignore
        private readonly HashSet<string> _ignoreDirs = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".vscode", ".idea",
            "bin", "obj", "packages",
            "node_modules", "bower_components",
            "wwwroot", "ClientApp",
            "TestResults", "artifacts",
            ".nuget", "publish"
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
        }

        public string WorkingDirectory => _workingDirectory;

        public void ChangeDirectory(string path)
        {
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
                throw new DirectoryNotFoundException($"Directory not found: {_workingDirectory}");
            }
        }

        public List<string> GetFileStructure()
        {
            var files = new List<string>();
            GetFilesRecursive(_workingDirectory, files, _workingDirectory);
            return files;
        }

        private void GetFilesRecursive(string path, List<string> files, string basePath)
        {
            try
            {
                // Get all files in current directory
                foreach (var file in Directory.GetFiles(path))
                {
                    var fileName = Path.GetFileName(file);
                    var extension = Path.GetExtension(file);

                    // Skip files with ignored extensions
                    if (_ignoreExtensions.Contains(extension))
                        continue;

                    // Skip generated files
                    if (fileName.EndsWith(".g.cs") || fileName.EndsWith(".designer.cs"))
                        continue;

                    var relativePath = Path.GetRelativePath(basePath, file);
                    files.Add(relativePath);
                }

                // Recursively process subdirectories
                foreach (var dir in Directory.GetDirectories(path))
                {
                    var dirName = Path.GetFileName(dir);

                    // Skip ignored directories
                    if (_ignoreDirs.Contains(dirName))
                        continue;

                    GetFilesRecursive(dir, files, basePath);
                }
            }
            catch (UnauthorizedAccessException)
            {
                // Silently skip directories we can't access
            }
        }

        public string ReadFile(string relativePath)
        {
            var fullPath = Path.Combine(_workingDirectory, relativePath);
            return File.Exists(fullPath) ? File.ReadAllText(fullPath) : null;
        }

        public void WriteFile(string relativePath, string content)
        {
            ValidateFilePath(relativePath);

            var fullPath = Path.Combine(_workingDirectory, relativePath);

            if (Directory.Exists(fullPath))
            {
                throw new InvalidOperationException($"Path '{fullPath}' is a directory, not a file.");
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            content = CleanCodeFromMarkdown(content);

            Console.WriteLine($"  Writing to: {relativePath}");
            File.WriteAllText(fullPath, content);
        }

        public void DeleteFile(string relativePath)
        {
            var fullPath = Path.Combine(_workingDirectory, relativePath);
            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
        }

        private void ValidateFilePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
            {
                throw new ArgumentException("File path cannot be empty");
            }

            var fileName = Path.GetFileName(relativePath);
            if (string.IsNullOrWhiteSpace(fileName) || !fileName.Contains('.'))
            {
                throw new InvalidOperationException($"Invalid file path '{relativePath}'. Must include a filename with extension.");
            }
        }

        private string CleanCodeFromMarkdown(string content)
        {
            // Remove markdown code block markers
            var pattern = @"^```[a-zA-Z]*\r?\n|^```\s*$";
            return Regex.Replace(content, pattern, "", RegexOptions.Multiline);
        }
    }
}
