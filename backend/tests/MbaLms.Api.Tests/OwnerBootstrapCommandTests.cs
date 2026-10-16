using MbaLms.Api.Infrastructure;

namespace MbaLms.Api.Tests;

public class OwnerBootstrapCommandTests
{
    [Fact]
    public void Password_flag_is_rejected_including_equals_form()
    {
        Assert.True(OwnerBootstrapCommand.HasDisallowedPasswordFlag(["--email", "owner@test.local", "--password", "Synthetic-Pass-1"]));
        Assert.True(OwnerBootstrapCommand.HasDisallowedPasswordFlag(["--password=Synthetic-Pass-1"]));
        Assert.False(OwnerBootstrapCommand.HasDisallowedPasswordFlag(["--email", "owner@test.local"]));
    }

    [Fact]
    public void ParseEmail_reads_the_value_after_the_flag()
    {
        Assert.Equal("owner@test.local", OwnerBootstrapCommand.ParseEmail(["--email", "owner@test.local"]));
        Assert.Null(OwnerBootstrapCommand.ParseEmail(["--email"]));
    }

    [Fact]
    public void PromptPassword_returns_null_when_confirmation_differs_and_does_not_use_password_flag()
    {
        var prompt = new FakeSecretPrompt(["Synthetic-Pass-1", "Synthetic-Pass-2"]);
        Assert.Null(OwnerBootstrapCommand.PromptPassword(prompt));
    }

    [Fact]
    public void PromptPassword_returns_synthetic_value_when_confirmation_matches()
    {
        const string synthetic = "Synthetic-Pass-1";
        var prompt = new FakeSecretPrompt([synthetic, synthetic]);
        Assert.Equal(synthetic, OwnerBootstrapCommand.PromptPassword(prompt));
    }

    private sealed class FakeSecretPrompt(IReadOnlyList<string> answers) : ISecretPrompt
    {
        private int _i;
        public bool CanPrompt => true;
        public string ReadSecret(string prompt) => answers[_i++];
    }
}
