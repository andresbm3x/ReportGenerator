using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Xml.Linq;
using Palmmedia.ReportGenerator.Core.Logging;
using Palmmedia.ReportGenerator.Core.Parser.Analysis;
using Palmmedia.ReportGenerator.Core.Parser.Filtering;
using Palmmedia.ReportGenerator.Core.Properties;

namespace Palmmedia.ReportGenerator.Core.Parser
{
    /// <summary>
    /// Parser for XML reports generated by CodeCoverage.exe.
    /// </summary>
    internal class DynamicCodeCoverageParser : ParserBase
    {
        /// <summary>
        /// The Logger.
        /// </summary>
        private static readonly ILogger Logger = LoggerFactory.GetLogger(typeof(DynamicCodeCoverageParser));

        /// <summary>
        /// Regex to analyze if a method name belongs to a lamda expression.
        /// </summary>
        private static Regex lambdaMethodNameRegex = new Regex("<.+>.+__", RegexOptions.Compiled);

        /// <summary>
        /// Regex to analyze if a method name is generated by compiler.
        /// </summary>
        private static Regex compilerGeneratedMethodNameRegex = new Regex(@"^.*<(?<CompilerGeneratedName>.+)>.+__.+$", RegexOptions.Compiled);

        /// <summary>
        /// Regex to extract short method name.
        /// </summary>
        private static Regex methodRegex = new Regex(@"^(?<MethodName>.+)\((?<Arguments>.*)\).*$", RegexOptions.Compiled);

        /// <summary>
        /// Initializes a new instance of the <see cref="DynamicCodeCoverageParser" /> class.
        /// </summary>
        /// <param name="assemblyFilter">The assembly filter.</param>
        /// <param name="classFilter">The class filter.</param>
        /// <param name="fileFilter">The file filter.</param>
        internal DynamicCodeCoverageParser(IFilter assemblyFilter, IFilter classFilter, IFilter fileFilter)
            : base(assemblyFilter, classFilter, fileFilter)
        {
        }

        /// <summary>
        /// Parses the given XML report.
        /// </summary>
        /// <param name="report">The XML report.</param>
        /// <returns>The parser result.</returns>
        public override ParserResult Parse(XContainer report)
        {
            if (report == null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            var assemblies = new List<Assembly>();

            var modules = report.Descendants("module")
              .Where(m => this.AssemblyFilter.IsElementIncludedInReport(m.Attribute("name").Value))
              .OrderBy(m => m.Attribute("name").Value)
              .ToArray();

            foreach (var module in modules)
            {
                assemblies.Add(this.ProcessAssembly(module));
            }

            var result = new ParserResult(assemblies.OrderBy(a => a.Name).ToList(), false, this.ToString());
            return result;
        }

        /// <summary>
        /// Processes the given assembly.
        /// </summary>
        /// <param name="module">The module.</param>
        /// <returns>The <see cref="Assembly"/>.</returns>
        private Assembly ProcessAssembly(XElement module)
        {
            string assemblyName = module.Attribute("name").Value;

            Logger.DebugFormat("  " + Resources.CurrentAssembly, assemblyName);

            var classNames = module
                .Elements("functions")
                .Elements("function")
                .Select(f => f.Attribute("type_name").Value)
                .Where(c => !c.Contains("<>")
                    && !c.StartsWith("$", StringComparison.OrdinalIgnoreCase))
                .Select(t =>
                {
                    int nestedClassSeparatorIndex = t.IndexOf('.');
                    return nestedClassSeparatorIndex > -1 ? t.Substring(0, nestedClassSeparatorIndex) : t;
                })
                .Distinct()
                .Where(c => this.ClassFilter.IsElementIncludedInReport(c))
                .OrderBy(name => name)
                .ToArray();

            var assembly = new Assembly(assemblyName);

            Parallel.ForEach(classNames, className => this.ProcessClass(module, assembly, className));

            return assembly;
        }

        /// <summary>
        /// Processes the given class.
        /// </summary>
        /// <param name="module">The module.</param>
        /// <param name="assembly">The assembly.</param>
        /// <param name="className">Name of the class.</param>
        private void ProcessClass(XElement module, Assembly assembly, string className)
        {
            var fileIdsOfClass = module
                .Elements("functions")
                .Elements("function")
                .Where(c => c.Attribute("type_name").Value.Equals(className, StringComparison.Ordinal)
                            || c.Attribute("type_name").Value.StartsWith(className + ".", StringComparison.Ordinal))
                .Elements("ranges")
                .Elements("range")
                .Select(r => r.Attribute("source_id").Value)
                .Distinct()
                .ToArray();

            var files = module
                .Elements("source_files")
                .Elements("source_file")
                .ToArray();

            var filteredFilesOfClass = fileIdsOfClass
                .Select(fileId =>
                    new
                    {
                        FileId = fileId,
                        FilePath = files.First(f => f.Attribute("id").Value == fileId).Attribute("path").Value
                    })
                .Where(f => this.FileFilter.IsElementIncludedInReport(f.FilePath))
                .ToArray();

            // If all files are removed by filters, then the whole class is omitted
            if (fileIdsOfClass.Length == 0 || filteredFilesOfClass.Length > 0)
            {
                var @class = new Class(className, assembly);

                foreach (var file in filteredFilesOfClass)
                {
                    @class.AddFile(ProcessFile(module, file.FileId, @class, file.FilePath));
                }

                assembly.AddClass(@class);
            }
        }

        /// <summary>
        /// Processes the file.
        /// </summary>
        /// <param name="module">The module.</param>
        /// <param name="fileId">The file id.</param>
        /// <param name="class">The class.</param>
        /// <param name="filePath">The file path.</param>
        /// <returns>The <see cref="CodeFile"/>.</returns>
        private static CodeFile ProcessFile(XElement module, string fileId, Class @class, string filePath)
        {
            var methods = module
                .Elements("functions")
                .Elements("function")
                .Where(c => c.Attribute("type_name").Value.Equals(@class.Name, StringComparison.Ordinal)
                            || c.Attribute("type_name").Value.StartsWith(@class.Name + ".", StringComparison.Ordinal))
                .Where(m => m.Elements("ranges").Elements("range").Any(r => r.Attribute("source_id").Value == fileId))
                .ToArray();

            var linesOfFile = methods
                .Elements("ranges")
                .Elements("range")
                .Select(l => new
                {
                    LineNumberStart = int.Parse(l.Attribute("start_line").Value, CultureInfo.InvariantCulture),
                    LineNumberEnd = int.Parse(l.Attribute("end_line").Value, CultureInfo.InvariantCulture),
                    Coverage = l.Attribute("covered").Value.Equals("no") ? 0 : 1,
                    Partial = l.Attribute("covered").Value.Equals("partial")
                })
                .OrderBy(seqpnt => seqpnt.LineNumberEnd)
                .ToArray();

            int[] coverage = new int[] { };
            LineVisitStatus[] lineVisitStatus = new LineVisitStatus[] { };

            if (linesOfFile.Length > 0)
            {
                coverage = new int[linesOfFile[linesOfFile.LongLength - 1].LineNumberEnd + 1];
                lineVisitStatus = new LineVisitStatus[linesOfFile[linesOfFile.LongLength - 1].LineNumberEnd + 1];

                for (int i = 0; i < coverage.Length; i++)
                {
                    coverage[i] = -1;
                }

                foreach (var seqpnt in linesOfFile)
                {
                    for (int lineNumber = seqpnt.LineNumberStart; lineNumber <= seqpnt.LineNumberEnd; lineNumber++)
                    {
                        coverage[lineNumber] = coverage[lineNumber] == -1 ? seqpnt.Coverage : Math.Min(coverage[lineNumber] + seqpnt.Coverage, 1);

                        if (lineVisitStatus[lineNumber] != LineVisitStatus.Covered)
                        {
                            LineVisitStatus statusOfLine = seqpnt.Partial ? LineVisitStatus.PartiallyCovered : (seqpnt.Coverage == 1 ? LineVisitStatus.Covered : LineVisitStatus.NotCovered);
                            lineVisitStatus[lineNumber] = (LineVisitStatus)Math.Max((int)lineVisitStatus[lineNumber], (int)statusOfLine);
                        }
                    }
                }
            }

            var codeFile = new CodeFile(filePath, coverage, lineVisitStatus);

            SetMethodMetrics(codeFile, methods);
            SetCodeElements(codeFile, methods);

            return codeFile;
        }

        /// <summary>
        /// Extracts the metrics from the given <see cref="XElement">XElements</see>.
        /// </summary>
        /// <param name="codeFile">The code file.</param>
        /// <param name="methodsOfFile">The methods of the file.</param>
        private static void SetMethodMetrics(CodeFile codeFile, IEnumerable<XElement> methodsOfFile)
        {
            foreach (var method in methodsOfFile)
            {
                string fullName = method.Attribute("name").Value;

                // Exclude properties and lambda expressions
                if (fullName.StartsWith("get_", StringComparison.Ordinal)
                    || fullName.StartsWith("set_", StringComparison.Ordinal)
                    || lambdaMethodNameRegex.IsMatch(fullName))
                {
                    continue;
                }

                fullName = ExtractMethodName(fullName, method.Attribute("type_name").Value);
                string shortName = methodRegex.Replace(fullName, m => string.Format(CultureInfo.InvariantCulture, "{0}({1})", m.Groups["MethodName"].Value, m.Groups["Arguments"].Value.Length > 0 ? "..." : string.Empty));

                var metrics = new[]
                {
                    new Metric(
                        ReportResources.BlocksCovered,
                        ParserBase.CodeCoverageUri,
                        MetricType.CoverageAbsolute,
                        int.Parse(method.Attribute("blocks_covered").Value, CultureInfo.InvariantCulture)),
                    new Metric(
                        ReportResources.BlocksNotCovered,
                        ParserBase.CodeCoverageUri,
                        MetricType.CoverageAbsolute,
                        int.Parse(method.Attribute("blocks_not_covered").Value, CultureInfo.InvariantCulture))
                };

                var methodMetric = new MethodMetric(fullName, shortName, metrics);

                var seqpnt = method
                    .Elements("ranges")
                    .Elements("range")
                    .FirstOrDefault();

                if (seqpnt != null)
                {
                    methodMetric.Line = int.Parse(seqpnt.Attribute("start_line").Value, CultureInfo.InvariantCulture);
                }

                codeFile.AddMethodMetric(methodMetric);
            }
        }

        /// <summary>
        /// Extracts the methods/properties of the given <see cref="XElement">XElements</see>.
        /// </summary>
        /// <param name="codeFile">The code file.</param>
        /// <param name="methodsOfFile">The methods of the file.</param>
        private static void SetCodeElements(CodeFile codeFile, IEnumerable<XElement> methodsOfFile)
        {
            foreach (var method in methodsOfFile)
            {
                if (lambdaMethodNameRegex.IsMatch(method.Attribute("name").Value))
                {
                    continue;
                }

                string methodName = ExtractMethodName(method.Attribute("name").Value, method.Attribute("type_name").Value);

                CodeElementType type = CodeElementType.Method;

                if (methodName.StartsWith("get_", StringComparison.OrdinalIgnoreCase)
                    || methodName.StartsWith("set_", StringComparison.OrdinalIgnoreCase))
                {
                    type = CodeElementType.Property;
                    methodName = methodName.Substring(4);
                }

                var seqpnts = method
                    .Elements("ranges")
                    .Elements("range")
                    .Select(l => new
                    {
                        LineNumberStart = int.Parse(l.Attribute("start_line").Value, CultureInfo.InvariantCulture),
                        LineNumberEnd = int.Parse(l.Attribute("end_line").Value, CultureInfo.InvariantCulture)
                    })
                    .ToArray();

                if (seqpnts.Length > 0)
                {
                    codeFile.AddCodeElement(new CodeElement(methodName, type, seqpnts.Min(s => s.LineNumberStart), seqpnts.Max(s => s.LineNumberEnd)));
                }
            }
        }

        /// <summary>
        /// Extracts the method name. For async methods the original name is returned.
        /// </summary>
        /// <param name="methodName">The full method name.</param>
        /// <param name="typeName">The type name.</param>
        /// <returns>The method name.</returns>
        private static string ExtractMethodName(string methodName, string typeName)
        {
            Match match = compilerGeneratedMethodNameRegex.Match(typeName);

            if (match.Success)
            {
                methodName = match.Groups["CompilerGeneratedName"].Value + "()";
            }

            return methodName;
        }
    }
}