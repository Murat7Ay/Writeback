using System;

namespace Writeback;

/// <summary>
/// Declares that the entity's table has triggers. SQL Server rejects <c>INSERT/UPDATE ... OUTPUT</c> (without
/// <c>INTO</c>) on tables with enabled triggers (error 334), so Writeback reads generated values back with
/// a trigger-safe re-select instead. Has no effect on other databases.
/// </summary>
/// <remarks>
/// This is the only Writeback-specific mapping attribute; everything else uses
/// <c>System.ComponentModel.DataAnnotations</c>. Prefer <c>options.Entity&lt;T&gt;(e =&gt; e.HasTriggers())</c>
/// to keep domain assemblies free of a Writeback reference.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public sealed class HasTriggersAttribute : Attribute
{
}
