using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace SilentCatchAnalyzer
{
    /// <summary>
    /// Flags a catch block that contains no statements (empty <c>catch { }</c>
    /// or comment-only). Such a block swallows its exception silently, which is
    /// exactly the failure-hiding pattern the backend hardening removed. Every
    /// catch must log (with the exception) or rethrow. A <c>throw;</c> counts as
    /// a statement, so rethrowing catches are not flagged.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class EmptyCatchAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "ATB0001";

        private static readonly DiagnosticDescriptor Rule = new DiagnosticDescriptor(
            id: DiagnosticId,
            title: "Catch block must not silently swallow exceptions",
            messageFormat: "Empty catch block silently swallows the exception, so log it with SilentCatch.Note or SilentCatch.Warn or an ILogger or rethrow",
            category: "Reliability",
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "A catch block with no statements hides failures. Every catch must log the exception with context or rethrow.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(Rule);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
            context.EnableConcurrentExecution();
            context.RegisterSyntaxNodeAction(AnalyzeCatch, SyntaxKind.CatchClause);
        }

        private static void AnalyzeCatch(SyntaxNodeAnalysisContext context)
        {
            var catchClause = (CatchClauseSyntax)context.Node;
            if (catchClause.Block != null && catchClause.Block.Statements.Count == 0)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, catchClause.CatchKeyword.GetLocation()));
            }
        }
    }
}
