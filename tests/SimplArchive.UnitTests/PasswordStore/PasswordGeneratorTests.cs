using SimplArchive.ApiClient.PasswordStore;

namespace SimplArchive.UnitTests.PasswordStore;

public sealed class PasswordGeneratorTests
{
    [Fact]
    public void A_default_password_is_twenty_characters_with_every_class()
    {
        var password = PasswordGenerator.Generate();

        Assert.Equal(20, password.Length);
        Assert.Contains(password, char.IsLower);
        Assert.Contains(password, char.IsUpper);
        Assert.Contains(password, char.IsDigit);
        Assert.Contains(password, c => !char.IsLetterOrDigit(c));
    }

    [Fact]
    public void Every_chosen_class_appears_even_at_the_shortest_length()
    {
        for (var i = 0; i < 200; i++)
        {
            var password = PasswordGenerator.Generate(new PasswordGeneratorOptions(Length: 4));
            Assert.True(password.Any(char.IsLower) && password.Any(char.IsUpper) && password.Any(char.IsDigit)
                && password.Any(c => !char.IsLetterOrDigit(c)), password);
        }
    }

    [Fact]
    public void Only_the_chosen_classes_are_drawn_and_look_alikes_can_be_avoided()
    {
        var digits = PasswordGenerator.Generate(new PasswordGeneratorOptions(Length: 64, Lowercase: false, Uppercase: false, Symbols: false));
        Assert.All(digits, c => Assert.True(char.IsDigit(c)));

        var clear = PasswordGenerator.Generate(new PasswordGeneratorOptions(Length: 512, AvoidAmbiguous: true));
        Assert.DoesNotContain(clear, c => "0O1lI|".Contains(c));
    }

    [Fact]
    public void Two_passwords_differ()
    {
        Assert.NotEqual(PasswordGenerator.Generate(), PasswordGenerator.Generate());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(PasswordGenerator.MaximumLength + 1)]
    public void A_length_that_cannot_hold_one_of_each_class_or_is_too_long_is_refused(int length) =>
        Assert.Throws<ArgumentException>(() => PasswordGenerator.Generate(new PasswordGeneratorOptions(Length: length)));

    [Fact]
    public void No_class_at_all_is_refused() =>
        Assert.Throws<ArgumentException>(() => PasswordGenerator.Generate(new PasswordGeneratorOptions(Lowercase: false, Uppercase: false, Digits: false, Symbols: false)));
}
