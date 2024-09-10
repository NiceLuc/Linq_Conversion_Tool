﻿using GVLinQOptimizer.CodeGeneration.Engine;
using GVLinQOptimizer.CodeGeneration.ViewModels;

namespace GVLinQOptimizer.CodeGeneration.Renderers;

[HandlebarsTemplateModel("Repository", "Repository.hbs", "{0}Repository.cs")]
internal class RepositoryRenderer : BaseRenderer<ContextDefinition>
{
    protected override async Task<object> ConvertToViewModelAsync(ITemplateEngine engine, ContextDefinition data, CancellationToken cancellationToken)
    {
        var viewModel = new RepositoryViewModel(data);
        foreach (var method in data.RepositoryMethods)
        {
            var model = data.DTOModels.FirstOrDefault(m => m.ClassName == method.ReturnType);
            var methodViewModel = new RepositoryMethodViewModel
            {
                IsList = method.IsList,
                MethodName = method.MethodName,
                ReturnType = method.ReturnType,
                SprocName = method.DatabaseName,
                DatabaseType = method.DatabaseType,
                Parameters = method.Parameters,
                Properties = model?.Properties ?? new(),
                SprocParameters = GetSprocParameters(method),
            };

            // todo: add support for ReturnValue parameter when NonQuery && MethodName.Contains("Insert")

            var resourceFileName = GetResourceFileName(methodViewModel, data);
            var code = await engine.ProcessAsync(resourceFileName, methodViewModel, cancellationToken);

            viewModel.Methods.Add(code);
        }

        return viewModel;
    }

    private List<ParameterViewModel> GetSprocParameters(MethodDefinition method)
    {
        return method.Parameters.Select(parameter => new ParameterViewModel
        {
            // method parameter details
            MethodParameterName = parameter.ParameterName,
            MethodParameterType = parameter.ParameterType,

            // sproc parameter details
            SprocParameterName = parameter.SprocParameterName,
            SprocParameterType = parameter.SqlDbType,
            SprocParameterDirection = parameter.ParameterDirection,
            SprocParameterLength = parameter.DatabaseLength,
            HasStringLength = HasStringLength(parameter),
            ShouldCaptureResult = parameter.IsRef,
            IsInputParameter = IsInputParameter(parameter)

        }).ToList();

        bool IsInputParameter(ParameterDefinition parameter) =>
            !parameter.IsRef && parameter.ParameterDirection.Equals("Input",
                StringComparison.InvariantCultureIgnoreCase);

        bool HasStringLength(ParameterDefinition parameter) =>
            !string.IsNullOrEmpty(parameter.DatabaseLength) &&
            !parameter.DatabaseLength.Equals("max",
                StringComparison.InvariantCultureIgnoreCase);

    }

    private static string GetResourceFileName(RepositoryMethodViewModel method, ContextDefinition data)
    {
        if (method.IsList)
            return "RepositoryMethodQueryMany.hbs";

        return data.DTOModels.Exists(m => m.ClassName == method.ReturnType)
            ? "RepositoryMethodQuerySingle.hbs" 
            : "RepositoryMethodNonQuery.hbs"; // example: int or bool or etc...
    }
}