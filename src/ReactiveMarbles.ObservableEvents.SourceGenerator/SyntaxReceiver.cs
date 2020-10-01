﻿// Copyright (c) 2020 ReactiveUI Association Inc. All rights reserved.
// ReactiveUI Association Inc licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace ReactiveMarbles.ObservableEvents.SourceGenerator
{
    internal class SyntaxReceiver : ISyntaxReceiver
    {
        public List<InvocationExpressionSyntax> InstanceCandidates { get; } = new List<InvocationExpressionSyntax>();

        public void OnVisitSyntaxNode(SyntaxNode syntaxNode)
        {
            if (syntaxNode is InvocationExpressionSyntax invocation)
            {
                InstanceCandidates.Add(invocation);
            }
        }
    }
}
