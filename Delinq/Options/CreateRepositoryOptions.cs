﻿using CommandLine;

namespace Delinq.Options;

[Verb("repo", HelpText = "Creates various files required for a repository based approach from an existing designer file.")]
internal class CreateRepositoryOptions
{
    [Value(0, Required = true, HelpText = "Full path to settings file.")]
    public string SettingsFilePath { get; set; }

    [Option('o', "output", Required = true, HelpText = "The output directory.")]
    public string OutputDirectory { get; set; }

    [Option('m', "method", HelpText = "Only generate code for a single method.")]
    public string MethodName { get; set; }
}
