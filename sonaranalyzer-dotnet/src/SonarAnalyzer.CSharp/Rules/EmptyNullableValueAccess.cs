/*
 * SonarAnalyzer for .NET
 * Copyright (C) 2015-2017 SonarSource SA
 * mailto: contact AT sonarsource DOT com
 *
 * This program is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this program; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.
 */

using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using SonarAnalyzer.Common;
using SonarAnalyzer.Helpers;
using SonarAnalyzer.Helpers.FlowAnalysis.Common;
using SonarAnalyzer.Helpers.FlowAnalysis.CSharp;

namespace SonarAnalyzer.Rules.CSharp
{
    using System.Collections.Immutable;
    using ExplodedGraph = Helpers.FlowAnalysis.CSharp.ExplodedGraph;

    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    [Rule(DiagnosticId)]
    public class EmptyNullableValueAccess : SonarDiagnosticAnalyzer
    {
        internal const string DiagnosticId = "S3655";
        private const string MessageFormat = "'{0}' is null on at least one execution path.";

        private static readonly DiagnosticDescriptor rule =
            DiagnosticDescriptorBuilder.GetDescriptor(DiagnosticId, MessageFormat, RspecStrings.ResourceManager);

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => ImmutableArray.Create(rule);

        private const string ValueLiteral = "Value";
        private const string HasValueLiteral = "HasValue";

        protected sealed override void Initialize(SonarAnalysisContext context)
        {
            context.RegisterExplodedGraphBasedAnalysis((e, c) => CheckEmptyNullableAccess(e, c));
        }

        private static void CheckEmptyNullableAccess(ExplodedGraph explodedGraph, SyntaxNodeAnalysisContext context)
        {
            var nullPointerCheck = new NullValueAccessedCheck(explodedGraph);
            explodedGraph.AddExplodedGraphCheck(nullPointerCheck);

            var nullIdentifiers = new HashSet<IdentifierNameSyntax>();

            EventHandler<MemberAccessedEventArgs> nullValueAccessedHandler =
                (sender, args) => nullIdentifiers.Add(args.Identifier);

            nullPointerCheck.ValuePropertyAccessed += nullValueAccessedHandler;

            try
            {
                explodedGraph.Walk();
            }
            finally
            {
                nullPointerCheck.ValuePropertyAccessed -= nullValueAccessedHandler;
            }

            foreach (var nullIdentifier in nullIdentifiers)
            {
                context.ReportDiagnostic(Diagnostic.Create(rule, nullIdentifier.Parent.GetLocation(), nullIdentifier.Identifier.ValueText));
            }
        }

        internal sealed class NullValueAccessedCheck : ExplodedGraphCheck
        {
            public event EventHandler<MemberAccessedEventArgs> ValuePropertyAccessed;

            public NullValueAccessedCheck(ExplodedGraph explodedGraph)
                : base(explodedGraph)
            {
            }

            private void OnValuePropertyAccessed(IdentifierNameSyntax identifier)
            {
                ValuePropertyAccessed?.Invoke(this, new MemberAccessedEventArgs
                {
                    Identifier = identifier
                });
            }

            public override ProgramState PreProcessInstruction(ProgramPoint programPoint, ProgramState programState)
            {
                var instruction = programPoint.Block.Instructions[programPoint.Offset];

                return instruction.IsKind(SyntaxKind.SimpleMemberAccessExpression)
                    ? ProcessMemberAccess(programState, (MemberAccessExpressionSyntax)instruction)
                    : programState;
            }

            private ProgramState ProcessMemberAccess(ProgramState programState, MemberAccessExpressionSyntax memberAccess)
            {
                var identifier = memberAccess.Expression.RemoveParentheses() as IdentifierNameSyntax;
                if (identifier == null ||
                    memberAccess.Name.Identifier.ValueText != ValueLiteral)
                {
                    return programState;
                }

                var symbol = semanticModel.GetSymbolInfo(identifier).Symbol;
                if (!IsNullableLocalScoped(symbol))
                {
                    return programState;
                }

                if (symbol.HasConstraint(ObjectConstraint.Null, programState))
                {
                    OnValuePropertyAccessed(identifier);
                    return null;
                }

                return programState;
            }

            private bool IsNullableLocalScoped(ISymbol symbol)
            {
                var type = symbol.GetSymbolType();
                return type != null &&
                    type.OriginalDefinition.Is(KnownType.System_Nullable_T) &&
                    explodedGraph.IsSymbolTracked(symbol);
            }

            private bool IsHasValueAccess(MemberAccessExpressionSyntax memberAccess)
            {
                return memberAccess.Name.Identifier.ValueText == HasValueLiteral &&
                    (semanticModel.GetTypeInfo(memberAccess.Expression).Type?.OriginalDefinition).Is(KnownType.System_Nullable_T);
            }

            internal bool TryProcessInstruction(MemberAccessExpressionSyntax instruction, ProgramState programState, out ProgramState newProgramState)
            {
                if (IsHasValueAccess(instruction))
                {
                    SymbolicValue sv;
                    newProgramState = programState.PopValue(out sv);
                    var nullableSymbolicValue = sv as NullableSymbolicValue;
                    if (nullableSymbolicValue != null)
                    {
                        newProgramState = newProgramState.PushValue(new HasValueAccessSymbolicValue(nullableSymbolicValue));
                        return true;
                    }
                }

                newProgramState = programState;
                return false;
            }
        }

        internal sealed class HasValueAccessSymbolicValue : MemberAccessSymbolicValue
        {
            public HasValueAccessSymbolicValue(NullableSymbolicValue nullable)
                : base(nullable, HasValueLiteral)
            {
            }

            public override IEnumerable<ProgramState> TrySetConstraint(SymbolicValueConstraint constraint, ProgramState currentProgramState)
            {
                var boolConstraint = constraint as BoolConstraint;
                if (boolConstraint == null)
                {
                    return new[] { currentProgramState };
                }

                var nullabilityConstraint = boolConstraint == BoolConstraint.True
                    ? ObjectConstraint.NotNull
                    : ObjectConstraint.Null;

                return MemberExpression.TrySetConstraint(nullabilityConstraint, currentProgramState);
            }
        }
    }
}