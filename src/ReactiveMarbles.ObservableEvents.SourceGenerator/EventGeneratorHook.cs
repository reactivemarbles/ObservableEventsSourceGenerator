﻿// Copyright (c) 2020 ReactiveUI Association Inc. All rights reserved.
// ReactiveUI Association Inc licenses this file to you under the MIT license.
// See the LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
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
        private const string ExtensionMethodText = @"
// <auto-generated />
using System;
namespace ReactiveMarbles.ObservableEvents
{
    /// <summary>
    /// Extension methods to generate IObservable for contained events on the class.
    /// </summary>
    internal static partial class ObservableGeneratorExtensions
    {
        /// <summary>
        /// Gets observable wrappers for all the events contained within the class.
        /// </summary>
        /// <returns>The events if available.</returns>
        public static NullEvents Events<T>(this T eventHost)
        {
            return default(NullEvents);
        }
    }

    /// <summary>
    /// Generates a IObservable`T` wrapper for the specified type.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    internal class GenerateStaticEventObservablesAttribute : Attribute
    {
        /// <summary>
        /// Initializes a new instance of the <see cref=""GenerateStaticEventObservablesAttribute""/> class.
        /// </summary>
        /// <param name=""type"">The static type to generate event observable wrappers for.</param>
        public GenerateStaticEventObservablesAttribute(Type type)
        {
            Type = type;
        }
        
        /// <summary>Gets the Type to generate the static event observable wrappers for.</summary>
        public Type Type { get; }
    }

    internal struct NullEvents
    {
    }
}";

        private static InstanceEventGenerator _eventGenerator = new InstanceEventGenerator();
        private static StaticEventGenerator _staticEventGenerator = new StaticEventGenerator();

        /// <inheritdoc />
        public void Execute(GeneratorExecutionContext context)
        {
            // add the attribute text.
            context.AddSource("TestExtensions.SourceGenerated.cs", SourceText.From(ExtensionMethodText, Encoding.UTF8));

            if (context.SyntaxReceiver is not SyntaxReceiver receiver)
            {
                return;
            }

            var compilation = context.Compilation;
            var options = (compilation as CSharpCompilation)?.SyntaxTrees[0].Options as CSharpParseOptions;
            compilation = context.Compilation.AddSyntaxTrees(CSharpSyntaxTree.ParseText(SourceText.From(ExtensionMethodText, Encoding.UTF8), options));

            var extensionMethodInvocations = new List<MethodDeclarationSyntax>();
            var staticMethodInvocations = new List<MethodDeclarationSyntax>();

            GetAvailableTypes(compilation, receiver, out var instanceNamespaceList, out var staticNamespaceList);

            GenerateEvents(context, _staticEventGenerator, true, staticNamespaceList, staticMethodInvocations);
            GenerateEvents(context, _eventGenerator, false, instanceNamespaceList, extensionMethodInvocations);

            GenerateEventExtensionMethods(context, extensionMethodInvocations);
        }

        /// <inheritdoc />
        public void Initialize(GeneratorInitializationContext context)
        {
            context.RegisterForSyntaxNotifications(() => new SyntaxReceiver());
        }

        private static void GenerateEventExtensionMethods(GeneratorExecutionContext context, List<MethodDeclarationSyntax> methodInvocationExtensions)
        {
            var classDeclaration = ClassDeclaration("ObservableGeneratorExtensions")
                .WithModifiers(TokenList(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.StaticKeyword), Token(SyntaxKind.PartialKeyword)))
                .WithMembers(List<MemberDeclarationSyntax>(methodInvocationExtensions));

            var namespaceDeclaration = NamespaceDeclaration(IdentifierName("ReactiveMarbles.ObservableEvents"))
                .WithMembers(SingletonList<MemberDeclarationSyntax>(classDeclaration));

            var compilationUnit = GenerateCompilationUnit(namespaceDeclaration);

            if (compilationUnit == null)
            {
                return;
            }

            context.AddSource("TestExtensions.FoundEvents.SourceGenerated.cs", SourceText.From(compilationUnit.NormalizeWhitespace().ToFullString(), Encoding.UTF8));
        }

        private static void GetAvailableTypes(
            Compilation compilation,
            SyntaxReceiver receiver,
            out List<(Location Location, INamedTypeSymbol NamedType)> instanceNamespaceList,
            out List<(Location Location, INamedTypeSymbol NamedType)> staticNamespaceList)
        {
            var observableGeneratorExtensions = compilation.GetTypeByMetadataName("ReactiveMarbles.ObservableEvents.ObservableGeneratorExtensions");

            if (observableGeneratorExtensions == null)
            {
                throw new InvalidOperationException("Cannot find ReactiveMarbles.ObservableEvents.ObservableGeneratorExtensions");
            }

            instanceNamespaceList = new List<(Location Location, INamedTypeSymbol NamedType)>();
            staticNamespaceList = new List<(Location Location, INamedTypeSymbol NamedType)>();

            foreach (var invocation in receiver.InstanceCandidates)
            {
                var semanticModel = compilation.GetSemanticModel(invocation.SyntaxTree);
                var methodSymbol = semanticModel.GetSymbolInfo(invocation).Symbol as IMethodSymbol;

                if (methodSymbol == null)
                {
                    continue;
                }

                if (!SymbolEqualityComparer.Default.Equals(methodSymbol.ContainingType, observableGeneratorExtensions))
                {
                    continue;
                }

                if (methodSymbol.TypeArguments.Length != 1)
                {
                    continue;
                }

                var callingSymbol = methodSymbol.TypeArguments[0] as INamedTypeSymbol;

                if (callingSymbol == null)
                {
                    continue;
                }

                var location = Location.Create(invocation.SyntaxTree, invocation.Span);

                instanceNamespaceList.Add((location, callingSymbol));
            }

            foreach (var attribute in compilation.Assembly.GetAttributes())
            {
                if (attribute.AttributeClass.ToString() != "ReactiveMarbles.ObservableEvents.GenerateStaticEventObservablesAttribute")
                {
                    continue;
                }

                if (attribute.ConstructorArguments.Length == 0)
                {
                    continue;
                }

                var type = attribute.ConstructorArguments[0].Value as INamedTypeSymbol;

                if (type == null)
                {
                    continue;
                }

                var location = attribute.ApplicationSyntaxReference == null ? Location.None : Location.Create(attribute.ApplicationSyntaxReference.SyntaxTree, attribute.ApplicationSyntaxReference.Span);

                staticNamespaceList.Add((location, type));
            }
        }

        private static bool GenerateEvents(
            GeneratorExecutionContext context,
            IEventSymbolGenerator symbolGenerator,
            bool isStatic,
            IReadOnlyList<(Location Location, INamedTypeSymbol NamedType)> symbols,
            List<MethodDeclarationSyntax>? methodInvocationExtensions = null)
        {
            var processedItems = new HashSet<INamedTypeSymbol>(TypeDefinitionNameComparer.Default);

            var fileType = isStatic ? "Static" : "Instance";

            var rootContainingSymbols = symbols.Select(x => x.NamedType).ToImmutableSortedSet(TypeDefinitionNameComparer.Default);

            foreach (var symbol in symbols)
            {
                bool hasEvents = false;
                var processingStack = new Stack<INamedTypeSymbol>(new[] { symbol.NamedType as INamedTypeSymbol });

                while (processingStack.Count != 0)
                {
                    var item = processingStack.Pop();

                    if (processedItems.Contains(item))
                    {
                        continue;
                    }

                    processedItems.Add(item);

                    var baseClassWithEvents = item.GetBasesWithCondition(RoslynHelpers.HasEvents).ToList();

                    var alwaysGenerate = rootContainingSymbols.Contains(item) && (baseClassWithEvents.Count != 0 || item.GetMembers<IEventSymbol>().Any());

                    var namespaceItem = symbolGenerator.Generate(item, alwaysGenerate);

                    foreach (var childItem in baseClassWithEvents)
                    {
                        processingStack.Push(childItem);
                    }

                    if (namespaceItem == null)
                    {
                        continue;
                    }

                    hasEvents = true;

                    var compilationUnit = GenerateCompilationUnit(namespaceItem);

                    if (compilationUnit == null)
                    {
                        continue;
                    }

                    var sourceText = compilationUnit.NormalizeWhitespace().ToFullString();

                    var name = $"SourceClass{item.ToDisplayString(RoslynHelpers.SymbolDisplayFormat)}-{fileType}Events.SourceGenerated.cs";

                    context.AddSource(
                        name,
                        SourceText.From(sourceText, Encoding.UTF8));

                    methodInvocationExtensions?.Add(MethodGenerator.GenerateMethod(item));
                }

                if (!hasEvents)
                {
                    context.ReportDiagnostic(Diagnostic.Create(new DiagnosticDescriptor("PHARM001", "Events not found", $"Could not find events on '{symbol.NamedType.ToDisplayString(RoslynHelpers.SymbolDisplayFormat)}' or its base classes.", "ObservableEventsGenerator", DiagnosticSeverity.Warning, true), symbol.Location));
                }
            }

            return true;
        }

        private static CompilationUnitSyntax? GenerateCompilationUnit(params NamespaceDeclarationSyntax?[] namespaceItems)
        {
            var processedItems = new List<NamespaceDeclarationSyntax>(namespaceItems.Length);
            for (int i = 0; i < namespaceItems.Length; ++i)
            {
                var namespaceItem = namespaceItems[i];

                if (namespaceItem == null)
                {
                    continue;
                }

                processedItems.Add(namespaceItem);
            }

            if (processedItems.Count == 0)
            {
                return null;
            }

            var members = processedItems.Count == 1 ? SingletonList<MemberDeclarationSyntax>(processedItems[0]) : List<MemberDeclarationSyntax>(processedItems);

            return CompilationUnit().WithMembers(members)
                .WithLeadingTrivia(
                    XmlSyntaxFactory.GenerateDocumentationString(
                        "<auto-generated />"));
        }
    }
}
