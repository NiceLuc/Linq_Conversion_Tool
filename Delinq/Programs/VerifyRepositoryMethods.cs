﻿using System.Data;
using System.Data.SqlClient;
using System.Reflection;
using System.Text.RegularExpressions;
using MediatR;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Options;

namespace Delinq.Programs;

public sealed class VerifyRepositoryMethods
{
    public record Request : IRequest<string>
    {
        public string ContextName { get; set; }
        public string BranchName { get; set; }
        public string RepositoryFilePath { get; set; }
        public string ConnectionString { get; set; }
        public string ValidationFilePath { get; set; }
        public string MethodName { get; set; }
    }

    public class Handler(
        IDefinitionSerializer<RepositoryDefinition> serializer,
        IDefinitionSerializer<ContextConfig> configSerializer,
        IOptions<ConnectionStrings> connectionStrings,
        IOptions<ProgramSettings> programSettings,
        IFileStorage fileStorage) : IRequestHandler<Request, string>
    {
        private readonly ConnectionStrings _connectionStrings = connectionStrings.Value;
        private readonly ProgramSettings _programSettings = programSettings.Value;

        public async Task<string> Handle(Request request, CancellationToken cancellationToken)
        {
            // if the connection string is a secret, replace it with the actual connection string
            ResolveConnectionString(request);

            // before doing anything more, ensure we have a valid connection string!
            await ValidateConnectionAsync(request.ConnectionString);

            var filePath = await ResolveRepositoryFilePathAsync(request, cancellationToken);
            var definition = new RepositoryDefinition {FilePath = filePath};

            // read repository file to extract all details using Roslyn (respecting method name filter)
            var methods = await ExtractRepositoryMethodsAsync(definition, request.MethodName, cancellationToken);

            foreach (var method in methods)
            {
                var repositoryMethod = ExtractRepositoryMethod(method, out var code);

                // read each method and extract the stored procedure details from database
                await ExtractSprocDetailsAsync(repositoryMethod, request.ConnectionString, cancellationToken);

                if (repositoryMethod.Status == RepositoryMethodStatus.Unknown)
                    FinalizeMethodStatus(repositoryMethod, code);

                definition.Methods.Add(repositoryMethod);
            }

            await serializer.SerializeAsync(request.ValidationFilePath, definition, cancellationToken);
            return request.ValidationFilePath;
        }

        private async Task<string> ResolveRepositoryFilePathAsync(Request request, CancellationToken cancellationToken)
        {
            if (!string.IsNullOrEmpty(request.RepositoryFilePath))
                return request.RepositoryFilePath;

            var executingDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var contextFilePath = Path.Combine(executingDirectory, "Configs", $"{request.ContextName}.json");
            var context = await configSerializer.DeserializeAsync(contextFilePath, cancellationToken);

            var branchName = string.IsNullOrEmpty(request.BranchName)
                ? _programSettings.DefaultBranchName
                : request.BranchName;

            var branchPath = _programSettings.TFSRootTemplate.Replace("{{BRANCH_NAME}}", request.BranchName);
            var path = Path.Combine(_programSettings.TFSRootTemplate, "Delinq", "Repositories", request.BranchName, "Repository.cs");
            if (!File.Exists(path))
                throw new FileNotFoundException($"File not found: {path}");

            return path;
        }

        #region Private Methods

        private void ResolveConnectionString(Request request)
        {
            if (string.IsNullOrEmpty(request.ConnectionString))
            {
                request.ConnectionString = _connectionStrings.InCode;
                return;
            }

            if (request.ConnectionString.StartsWith("SECRET:"))
            {
                if (request.ConnectionString != "SECRET:ConnectionStrings:InCode")
                    throw new InvalidOperationException("Must specify 'SECRET:ConnectionStrings:InCode'");

                request.ConnectionString = _connectionStrings.InCode;
            }
        }

        private static async Task ValidateConnectionAsync(string connectionString)
        {
            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync();
            connection.Close();
        }

        private async Task<IEnumerable<MethodDeclarationSyntax>> ExtractRepositoryMethodsAsync(RepositoryDefinition definition, string? methodName, CancellationToken cancellationToken)
        {
            // read the repository file
            var code = await fileStorage.ReadAllTextAsync(definition.FilePath, cancellationToken);

            // get the methods from the code
            var tree = CSharpSyntaxTree.ParseText(code);
            var root = (CompilationUnitSyntax) await tree.GetRootAsync(cancellationToken);
            var methods = root.DescendantNodes().OfType<MethodDeclarationSyntax>()
                .Where(m => m.Modifiers.ToString() == "public");

            // return one or all methods
            return !string.IsNullOrEmpty(methodName)
                ? methods.Where(m => m.Identifier.Text == methodName)
                : methods;
        }

        private static RepositoryMethod ExtractRepositoryMethod(MethodDeclarationSyntax method, out string code)
        {
            var parameters = method.ParameterList.Parameters.Select(p => new RepositoryParameter
            {
                Name = p.Identifier.Text,
                Type = p.Type?.ToString() ?? "UNKNOWN",
                Modifier = p.Modifiers.ToString()
            }).ToList();

            var result = new RepositoryMethod
            {
                Name = method.Identifier.Text,
                ReturnType = method.ReturnType.ToString(),
                Status = RepositoryMethodStatus.Unknown,
                Parameters = parameters,
            };

            code = method.Body?.ToString() ?? string.Empty;
            if (string.IsNullOrEmpty(code))
                throw new InvalidOperationException("Method body is empty!");

            // extend our method definition with details from the code
            result.QueryType = GetCurrentQueryType(code);

            // every repository should have a stored procedure name
            var match = Regex.Match(code, "SQL = \"(.*?)\"");
            var sprocName = match.Success ? match.Groups[1].Value : string.Empty;
            result.StoredProcedure = new SprocDefinition {Name = sprocName, QueryType = SprocQueryType.Unknown};

            return result;
        }

        private static async Task ExtractSprocDetailsAsync(RepositoryMethod method, string connectionString, CancellationToken cancellationToken)
        {
            var sproc = method.StoredProcedure;
            var sprocName = sproc.Name.Replace("dbo.", "");
            var code = await GetStoredProcedureCodeAsync(connectionString, sprocName, cancellationToken);

            if (string.IsNullOrEmpty(code))
            {
                method.Status = RepositoryMethodStatus.SprocNotFound;
                sproc.QueryType = method.QueryType;
                return;
            }

            var splitIndex = code.IndexOf("BEGIN", StringComparison.Ordinal);
            if (splitIndex < 0)
            {
                splitIndex = code.IndexOf("AS", StringComparison.Ordinal);
                if (splitIndex < 0)
                    throw new InvalidOperationException("Sproc is not valid: " + sprocName);
            }

            var header = code[..splitIndex];
            var body = code[splitIndex..];

            sproc.Parameters = ExtractSprocParameters(header);
            sproc.QueryType = GetSprocQueryType(body);
        }

        private static void FinalizeMethodStatus(RepositoryMethod method, string code)
        {
            var sproc = method.StoredProcedure;
            if (method.QueryType != sproc.QueryType)
            {
                method.Status = RepositoryMethodStatus.InvalidQueryType;
                method.Errors.Add($"C#: {method.QueryType}, DB: {sproc.QueryType}");
            }

            if (method.Parameters.Count != sproc.Parameters.Count)
            {
                method.Status = method.Parameters.Count > sproc.Parameters.Count
                    ? RepositoryMethodStatus.TooManySprocParameters
                    : RepositoryMethodStatus.MissingSprocParameters;
                method.Errors.Add($"C#: {method.Parameters.Count}, DB: {sproc.Parameters.Count}");
            }

            // validate each sproc parameter
            foreach (var parameter in sproc.Parameters)
            {
                var pattern = @$"AddParameter\(""@{parameter.Name}""";
                if (Regex.IsMatch(code, pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase))
                    continue;

                method.Status = RepositoryMethodStatus.NotAllParametersAreBeingSet;
                method.Errors.Add($"Does not pass @{parameter.Name} parameter to sproc.");
            }

            // if we've made it here, we have done the best we can to determine the status
            if (method.Errors.Count == 0)
                method.Status = RepositoryMethodStatus.OK;
        }


        private static SprocQueryType GetCurrentQueryType(string? body)
        {
            if (string.IsNullOrEmpty(body))
                return SprocQueryType.Unknown;

            if (body.Contains("ReturnValue"))
                return SprocQueryType.ReturnValue;

            if (body.Contains("query.Read()"))
                return SprocQueryType.Query;

            if (body.Contains("ExecuteNonQuery"))
                return SprocQueryType.NonQuery;

            // note: we don't currently have code for Scalar in our query methods

            return SprocQueryType.Unknown;
        }

        private static async Task<string?> GetStoredProcedureCodeAsync(string connectionString, string sprocName, CancellationToken cancellationToken)
        {
            const string query = """
                 SELECT sm.definition
                 FROM sys.sql_modules AS sm
                 INNER JOIN sys.objects AS o ON sm.object_id = o.object_id
                 WHERE o.type = 'P' AND o.name = @StoredProcedureName
            """;

            await using var connection = new SqlConnection(connectionString);
            await using var command = new SqlCommand(query, connection);
            command.Parameters.Add(new SqlParameter("@StoredProcedureName", SqlDbType.NVarChar, 128));
            command.Parameters["@StoredProcedureName"].Value = sprocName;

            await connection.OpenAsync(cancellationToken);
            var result = await command.ExecuteScalarAsync(cancellationToken);
            return result?.ToString();
        }

        private static List<SprocParameter> ExtractSprocParameters(string code)
        {
            const string pattern = @"@(?<ParamName>\w+)\s+(?<DataType>[\w\(\)]+)\s*(?<Collation>COLLATE\s+\w+)?\s*(?<Output>OUT|OUTPUT)?\s*(?<ReadOnly>READONLY)?";
            var results = new List<SprocParameter>();

            var regex = new Regex(pattern);
            foreach (Match match in regex.Matches(code))
            {
                var parameter = new SprocParameter
                {
                    Name = match.Groups["ParamName"].Value,
                    Type = match.Groups["DataType"].Value,
                    Modifier = match.Groups["Output"].Value
                };

                results.Add(parameter);
            }

            return results;
        }

        private static SprocQueryType GetSprocQueryType(string code)
        {
            // Check for RETURN statements
            var returnRegex = new Regex(@"\bRETURN\b\s+(@|\d|SCOPE_IDENTITY)\d*", RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (returnRegex.IsMatch(code))
                return SprocQueryType.ReturnValue;

            // Check for SELECT statements that return result sets
            // note: regex considers very complex SELECT statements 
            var pattern = @"(?<!INSERT\s+INTO[\s\S]+?\)\s*)(?<!EXISTS\s*\()(?<!\()\bSELECT\b\s+(?!\@|\*\s+INTO|\@\w+\s*=)";
            var selectRegex = new Regex(pattern, RegexOptions.Multiline | RegexOptions.IgnoreCase);
            if (selectRegex.IsMatch(code))
                return SprocQueryType.Query;

            return SprocQueryType.NonQuery;
        }

        #endregion
    }
}