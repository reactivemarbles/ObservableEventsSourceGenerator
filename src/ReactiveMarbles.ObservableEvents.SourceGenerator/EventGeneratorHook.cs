﻿// Copyright (c) 2020 ReactiveUI Association Inc. All rights reserved.
// ReactiveUI Association Inc licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

using ReactiveMarbles.ObservableEvents.SourceGenerator.EventGenerators;
using ReactiveMarbles.ObservableEvents.SourceGenerator.EventGenerators.Generators;

using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace ReactiveMarbles.ObservableEvents.SourceGenerator
{
    /// <summary>
    /// Generates Observables from events in specified types and namespaces.
    /// </summary>
    [Generator]
    public class EventGeneratorHook : ISourceGenerator
    {
        private const string AttributeText = @"// <auto-generated />
using System;
namespace ReactiveMarbles.ObservableEvents
{
    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct)]
    public class TypeEventsToObservablesAttribute : Attribute
    {
        public TypeEventsToObservablesAttribute()
        {
        }

        public TypeEventsToObservablesAttribute(Type type)
        {
            Type = type;
        }

        public Type Type { get; }
    }
}";

        private static InstanceEventGenerator _eventGenerator = new InstanceEventGenerator();
        private static StaticEventGenerator _staticEventGenerator = new StaticEventGenerator();

        private static SymbolDisplayFormat _symbolDisplayFormat = new SymbolDisplayFormat(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces);

        /// <inheritdoc />
        public void Execute(GeneratorExecutionContext context)
        {
            // add the attribute text.
            context.AddSource("EventsToObservablesAttribute.SourceGenerated.cs", SourceText.From(AttributeText, Encoding.UTF8));

            // retreive the populated receiver.
            if (context.SyntaxReceiver is not SyntaxReceiver receiver)
            {
                return;
            }

            var options = (context.Compilation as CSharpCompilation)?.SyntaxTrees[0]?.Options as CSharpParseOptions;

            var compilation = context.Compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(SourceText.From(AttributeText, Encoding.UTF8), options));

            GetAvailableTypes(receiver, compilation, out var instanceNamespaceList, out var staticNamespaceList);

            foreach (var namespaceItem in staticNamespaceList)
            {
                GenerateEvents(context, _staticEventGenerator, namespaceItem.Key, "Static", namespaceItem.Value);
            }

            foreach (var namespaceItem in instanceNamespaceList)
            {
                GenerateEvents(context, _eventGenerator, namespaceItem.Key, "Instance", namespaceItem.Value);
            }
        }

        /// <inheritdoc />
        public void Initialize(GeneratorInitializationContext context)
        {
            // Register a syntax receiver that will be created for each generation pass
            context.RegisterForSyntaxNotifications(() => new SyntaxReceiver());
        }

        private static void GetAvailableTypes(SyntaxReceiver receiver, Compilation compilation, out SortedDictionary<string, List<INamedTypeSymbol>> instanceNamespaceList, out SortedDictionary<string, List<INamedTypeSymbol>> staticNamespaceList)
        {
            instanceNamespaceList = new SortedDictionary<string, List<INamedTypeSymbol>>();
            staticNamespaceList = new SortedDictionary<string, List<INamedTypeSymbol>>();
            for (int i = 0; i < receiver.CandidateTypes.Count; ++i)
            {
                var candidate = receiver.CandidateTypes[i];

                var rootSymbol = compilation.GetSemanticModel(candidate.SyntaxTree)?.GetDeclaredSymbol(candidate);

                if (rootSymbol == null)
                {
                    continue;
                }

                var namespaceDictionary = rootSymbol.IsStatic ? staticNamespaceList : instanceNamespaceList;

                var symbolList = rootSymbol.GetBaseTypesAndThis(RoslynHelpers.HasEvents);

                foreach (var symbol in symbolList)
                {
                    if (!namespaceDictionary.TryGetValue(symbol.ContainingNamespace.Name, out var list))
                    {
                        list = new List<INamedTypeSymbol>();
                        namespaceDictionary[symbol.ContainingNamespace.ToDisplayString(_symbolDisplayFormat)] = list;
                    }

                    int index = list.BinarySearch(symbol, TypeDefinitionNameComparer.Default);

                    if (index < 0)
                    {
                        list.Insert(~index, symbol);
                    }
                }
            }
        }

        private static void GenerateEvents(GeneratorExecutionContext context, IEventSymbolGenerator symbolGenerator, string namespaceName, string suffixName, List<INamedTypeSymbol> symbols)
        {
            var namespaceSyntax = symbolGenerator.Generate(namespaceName, symbols);

            if (namespaceSyntax == null)
            {
                return;
            }

            var compilationUnit = CompilationUnit().WithMembers(SingletonList<MemberDeclarationSyntax>(namespaceSyntax))
                .WithLeadingTrivia(
                    XmlSyntaxFactory.GenerateDocumentationString(
                        "<auto-generated />"));

            var name = $"SourceClass{namespaceName}{suffixName}.SourceGenerated.Designer.cs";

            var sourceText = compilationUnit.NormalizeWhitespace().ToFullString();
            context.AddSource(
                name,
                SourceText.From(sourceText, Encoding.UTF8));

            File.WriteAllText(Path.Combine("C:/Temp", name), sourceText);
        }
    }
}