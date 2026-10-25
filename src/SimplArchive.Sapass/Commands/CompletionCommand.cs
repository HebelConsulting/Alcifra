using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace SimplArchive.Sapass.Commands;

public sealed class CompletionSettings : CommandSettings
{
    [CommandArgument(0, "<shell>")]
    [Description("bash, zsh or fish.")]
    public string Shell { get; init; } = string.Empty;

    public override ValidationResult Validate() => Shell is "bash" or "zsh" or "fish"
        ? ValidationResult.Success()
        : ValidationResult.Error("Supported shells: bash, zsh, fish.");
}

/// <summary>
/// Prints the shell script that completes store names and folder/entry paths — pass's model: the script calls the
/// binary back with the words typed so far, and the binary answers with candidates.
/// </summary>
/// <remarks>
/// Install it where the shell looks, e.g. <c>sapass completion zsh &gt; ~/.zfunc/_sapass</c> for zsh with
/// <c>fpath=(~/.zfunc $fpath)</c>, or <c>sapass completion bash | sudo tee /etc/bash_completion.d/sapass</c>.
/// The output is data (ADR 0906): nothing else goes to stdout.
/// </remarks>
public sealed class CompletionCommand : Command<CompletionSettings>
{
    protected override int Execute(CommandContext context, CompletionSettings settings, CancellationToken cancellationToken)
    {
        // A trailing newline, so a redirected script ends its last line properly.
        Console.Out.Write(settings.Shell switch
        {
            "bash" => $"{BashScript}\n",
            "zsh" => $"{ZshScript}\n",
            _ => $"{FishScript}\n",
        });
        return 0;
    }

    // ${COMP_WORDS[@]:1:$COMP_CWORD} is the argv so far (the program name dropped), which is exactly what
    // __complete expects; -o default keeps file completion for a word that names a file.
    private const string BashScript = """
        _sapass_complete()
        {
            local candidates
            candidates=$(sapass __complete "${COMP_WORDS[@]:1:$COMP_CWORD}" 2>/dev/null)
            COMPREPLY=($(compgen -W "$candidates" -- "${COMP_WORDS[COMP_CWORD]}"))
        }
        complete -o default -o bashdefault -F _sapass_complete sapass
        """;

    // words[2,-1] drops the program name; -Q would quote, which paths do not want.
    private const string ZshScript = """
        #compdef sapass
        _sapass()
        {
            local -a candidates
            candidates=(${(f)"$(sapass __complete ${words[2,-1]} 2>/dev/null)"})
            compadd -a candidates
        }
        compdef _sapass sapass
        """;

    private const string FishScript = """
        # sapass: complete commands, stores and entry paths.
        complete -c sapass -f -a "(sapass __complete (commandline -opc)[2..-1] (commandline -ct) 2>/dev/null)"
        """;
}
