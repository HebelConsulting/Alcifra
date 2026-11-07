using System.ComponentModel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Alcifra.Pass.Commands;

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
/// Install it with one line in the shell's start-up file: <c>eval "$(apass completion zsh)"</c> in <c>~/.zshrc</c>, or
/// <c>eval "$(apass completion bash)"</c> in <c>~/.bashrc</c>. Until then a TAB after <c>apass</c> completes local files.
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
    // __complete expects. No -o default: no command takes a local file, and with it a word no store name matches
    // completed to a local directory. -o nospace with the space added by hand, because a folder is not the end of a path
    // and macOS's bash has no compopt to decide that per candidate.
    private const string BashScript = """
        _apass_complete()
        {
            local candidates
            candidates=$(apass __complete "${COMP_WORDS[@]:1:$COMP_CWORD}" 2>/dev/null)
            COMPREPLY=($(compgen -W "$candidates" -- "${COMP_WORDS[COMP_CWORD]}"))
            if [ "${#COMPREPLY[@]}" -eq 1 ] && [ "${COMPREPLY[0]%/}" = "${COMPREPLY[0]}" ]; then
                COMPREPLY[0]="${COMPREPLY[0]} "
            fi
        }
        complete -o nospace -F _apass_complete apass
        """;

    // words[2,-1] drops the program name, and quoted with (@) it keeps an EMPTY last word: unquoted, "apass ls <TAB>"
    // reached the binary as "ls" alone and was answered as the first word. A folder takes no space after it, so the next
    // TAB goes on inside it. compinit is loaded where the start-up file has not, because compdef needs it.
    private const string ZshScript = """
        #compdef apass
        _apass()
        {
            local -a candidates folders others
            candidates=(${(f)"$(apass __complete "${(@)words[2,-1]}" 2>/dev/null)"})
            folders=(${(M)candidates:#*/})
            others=(${candidates:#*/})
            (( $#folders )) && compadd -S '' -a folders
            (( $#others )) && compadd -a others
            return 0
        }
        (( $+functions[compdef] )) || { autoload -Uz compinit && compinit }
        compdef _apass apass
        """;

    private const string FishScript = """
        # apass: complete commands, stores and entry paths.
        complete -c apass -f -a "(apass __complete (commandline -opc)[2..-1] (commandline -ct) 2>/dev/null)"
        """;
}
