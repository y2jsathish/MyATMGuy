using ATM.Application.Correlation;
using ATM.Application.Errors;
using ATM.Application.Masking;
using ATM.Core.Errors;

namespace ATM.Application.Tests;

public class MaskingTests
{
    [Theory]
    [InlineData("Card 4111111111111111 used", "Card 411111******1111 used")]
    [InlineData("pan=4111 1111 1111 1111", "pan=411111******1111")]
    [InlineData("pan=4111-1111-1111-1111", "pan=411111******1111")]
    [InlineData("5500005555555559", "550000******5559")]
    [InlineData("340000000000009", "340000*****0009")]
    [InlineData("6011000000000004", "601100******0004")]
    [InlineData("3530111333300000", "353011******0000")]
    [InlineData("4222222222222", "422222***2222")]
    [InlineData("4012888888881881", "401288******1881")]
    [InlineData("{\"Pan\":\"4111111111111111\"}", "{\"Pan\":\"411111******1111\"}")]
    [InlineData("a=4111111111111111;b=5500005555555559", "a=411111******1111;b=550000******5559")]
    public void Pan_IsReducedToFirstSixAndLastFour(string input, string expected) =>
        SensitiveDataMasker.Mask(input).Should().Be(expected);

    [Theory]
    [InlineData("pin=1234")]
    [InlineData("PIN: 1234")]
    [InlineData("\"pin\":\"1234\"")]
    [InlineData("pinblock=0123456789ABCDEF")]
    [InlineData("pin_block=0123456789ABCDEF")]
    [InlineData("cvv=123")]
    [InlineData("CVV2: 123")]
    [InlineData("cvc=987")]
    [InlineData("password=hunter2")]
    [InlineData("Password: hunter2")]
    [InlineData("pwd=hunter2")]
    [InlineData("secret=abc123")]
    [InlineData("client_secret=abc123")]
    [InlineData("token=abc.def.ghi")]
    [InlineData("access_token=abc123")]
    [InlineData("apikey=abc123")]
    [InlineData("api-key: abc123")]
    [InlineData("\"ApiKey\": \"abc123\"")]
    [InlineData("private_key=MIIE")]
    [InlineData("ConnectionString=Server=x;Password=hunter2")]
    public void SecretsAndPins_AreRedacted(string input)
    {
        var masked = SensitiveDataMasker.Mask(input);

        masked.Should().Contain(SensitiveDataMasker.Redacted);
        masked.Should().NotContain("1234").And.NotContain("hunter2").And.NotContain("abc123").And.NotContain("0123456789ABCDEF").And.NotContain("987");
    }

    [Theory]
    [InlineData("%B4111111111111111^DOE/JOHN^25121010000000000000?")]
    [InlineData(";4111111111111111=25121010000000000000?")]
    public void TrackData_IsRemoved(string input) =>
        SensitiveDataMasker.Mask(input).Should().Be("[TRACK]");

    [Fact]
    public void BearerToken_IsRedacted() =>
        SensitiveDataMasker.Mask("Authorization header Bearer abc.DEF-123").Should().NotContain("abc.DEF-123");

    [Theory]
    [InlineData("Order 4111111111111112 failed")]
    [InlineData("Transaction 20260101123456 done")]
    [InlineData("Terminal ATM-0001 online")]
    [InlineData("")]
    public void OrdinaryText_IsUntouched(string input) =>
        SensitiveDataMasker.Mask(input).Should().Be(input);

    [Fact]
    public void Null_BecomesEmpty() => SensitiveDataMasker.Mask(null).Should().BeEmpty();
}

public class CorrelationTests
{
    [Fact]
    public async Task Id_FlowsAcrossAwaitAndChildTasks()
    {
        using var scope = CorrelationContext.Begin();
        var id = CorrelationContext.Current;

        await Task.Delay(5);
        var inChild = await Task.Run(() => CorrelationContext.Current);

        CorrelationContext.Current.Should().Be(id);
        inChild.Should().Be(id);
    }

    [Fact]
    public void Scope_RestoresPreviousValue()
    {
        using (CorrelationContext.Begin())
        {
            var outer = CorrelationContext.Current;
            using (CorrelationContext.Begin())
            {
                CorrelationContext.Current.Should().NotBe(outer);
            }

            CorrelationContext.Current.Should().Be(outer);
        }

        CorrelationContext.Current.Should().BeNull();
    }
}

public class ErrorCounterTests
{
    [Fact]
    public void Counts_PerCode()
    {
        var counters = new ErrorCounters();
        counters.Record(ErrorCatalogue.DeviceFault);
        counters.Record(ErrorCatalogue.DeviceFault);
        counters.Record(ErrorCatalogue.Unknown);

        counters.Total.Should().Be(3);
        counters.Snapshot()["ATM-4001"].Should().Be(2);
    }
}
