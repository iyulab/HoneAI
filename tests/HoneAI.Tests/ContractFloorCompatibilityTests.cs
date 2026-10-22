using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HoneAI;
using Xunit;

namespace HoneAI.Tests;

/// <summary>
/// Enforces the additive-only compatibility policy for the contract floor — the
/// types a consumer can take on their own, with no other HoneAI package:
/// <see cref="ITracedPrediction{T}"/>, <see cref="PredictionProvenance"/>,
/// <see cref="IReviewStore"/>, <see cref="IJudge{TInput,TValue}"/>, <see cref="JudgeKind"/>. Within 0.x these may grow
/// (a nullable property, an enum value, a default interface method) but must not change
/// in a way that breaks code already written against them.
/// </summary>
/// <remarks>
/// The policy is stated in the package README; this class is what makes it checkable.
/// Each test pins the exact set of members whose addition <em>would</em> break a consumer:
/// a new <c>required</c> member breaks every object initializer, a new abstract interface
/// member breaks every implementation, a renumbered enum value breaks every persisted
/// provenance record. Loosening one of these is a deliberate breaking change — update the
/// pin, the CHANGELOG migration note, and the README together.
/// </remarks>
public class ContractFloorCompatibilityTests
{
    [Fact]
    public void PredictionProvenance_RequiredMembersArePinned()
    {
        // Only the kind must be supplied by an object initializer. Every other member is optional
        // so a provenance record written today still compiles tomorrow. (0.3.0 re-declared this
        // floor: SourceLayer + Confidence became Kind, with Confidence optional.)
        var required = typeof(PredictionProvenance)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<RequiredMemberAttribute>() is not null)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Kind" }, required);
    }

    [Fact]
    public void PredictionProvenance_OptionalMembersAreNullableOrDefaultable()
    {
        // A non-required property must be safe to leave out: either a reference/nullable
        // type, or a value type whose default is a meaningful "not set" (bool Unverified).
        var optional = typeof(PredictionProvenance)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetCustomAttribute<RequiredMemberAttribute>() is null);

        var context = new NullabilityInfoContext();
        foreach (var property in optional)
        {
            var isNullable = context.Create(property).WriteState == NullabilityState.Nullable
                             || Nullable.GetUnderlyingType(property.PropertyType) is not null;
            var isBool = property.PropertyType == typeof(bool);
            Assert.True(isNullable || isBool,
                $"{property.Name} is optional but neither nullable nor bool — leaving it out would silently mean a real value.");
        }
    }

    [Fact]
    public void PredictionProvenance_AnnotationValuesArePinnedToString()
    {
        // The extension point is a flat map of opaque scalar strings, and the README says so.
        // Widening the value type (to object, or to a serializer's node type) would break every
        // object initializer already written against it, put a serializer into a contract that
        // has no dependencies, and cost the read side its round-trip: a value written as one
        // type would come back as whatever the deserializer chose. A structured value is
        // encoded by the consumer that writes it.
        var annotations = typeof(PredictionProvenance).GetProperty(nameof(PredictionProvenance.Annotations));
        Assert.NotNull(annotations);

        var type = annotations!.PropertyType;
        Assert.True(type.IsGenericType);
        Assert.Equal(typeof(IReadOnlyDictionary<,>), type.GetGenericTypeDefinition());
        Assert.Equal(new[] { typeof(string), typeof(string) }, type.GetGenericArguments());
    }

    [Fact]
    public void IReviewStore_AbstractMembersArePinned()
    {
        // A consumer that implements the review store over its own database must keep compiling.
        // New capability goes in as a default interface method, which IsAbstract excludes.
        // (0.9.0 re-declared this floor: the in-memory wait IHitlGate became the stored review IReviewStore.)
        var abstractMembers = typeof(IReviewStore)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.IsAbstract)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "DecideAsync", "GetAsync", "ListPendingAsync", "OpenAsync" }, abstractMembers);
    }

    [Fact]
    public void ITracedPrediction_MembersArePinned()
    {
        // Two members, both read-only: the value and how it was made.
        var members = typeof(ITracedPrediction<>)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "Provenance", "Value" }, members);
        Assert.DoesNotContain(
            typeof(ITracedPrediction<>).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            m => !m.IsSpecialName);
    }

    [Fact]
    public void JudgeKind_ExistingValuesKeepTheirNumbers()
    {
        // Provenance records are persisted (JSONL sink); the numbers behind these names are part
        // of the on-disk contract. New kinds may be added, these may not move. The first four are
        // the numbers of the 0.2.x layers they replaced.
        Assert.Equal(0, (int)JudgeKind.Theory);
        Assert.Equal(1, (int)JudgeKind.Statistics);
        Assert.Equal(2, (int)JudgeKind.Ml);
        Assert.Equal(3, (int)JudgeKind.Llm);
        Assert.Equal(4, (int)JudgeKind.System1);
        Assert.Equal(5, (int)JudgeKind.Vlm);
        Assert.Equal(6, (int)JudgeKind.Human);
    }

    [Fact]
    public void IJudge_AbstractMembersArePinned()
    {
        // A consumer that wraps its own engine as a judge must keep compiling.
        var type = typeof(IJudge<,>);
        var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.IsAbstract)
            .Select(m => m.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { "JudgeAsync", "get_Tier" }, methods);
    }
}
