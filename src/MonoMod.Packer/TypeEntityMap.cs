﻿using AsmResolver;
using AsmResolver.DotNet;
using System.Collections.Generic;
using MonoMod.Packer.Entities;
using System;
using System.Collections.Concurrent;
using MonoMod.Packer.Utilities;
using System.Diagnostics.CodeAnalysis;
using MonoMod.Packer.Diagnostics;

namespace MonoMod.Packer {

    internal readonly record struct NullableUtf8String(Utf8String? Value) {
        public static implicit operator NullableUtf8String(Utf8String? val) => new(val);
    }

    internal sealed class TypeEntityMap {
        public readonly PackOptions Options;
        public readonly IMetadataResolver MdResolver;
        public readonly IMetadataResolver ExternalMdResolver;
        public readonly DiagnosticTranslator Diagnostics;

        private readonly Dictionary<TypeDefinition, TypeEntity> entityMap = new();
        private readonly Dictionary<NullableUtf8String, Dictionary<NullableUtf8String, UnifiedTypeEntity>> entitiesByName = new();

        private readonly ConcurrentBag<TypesInSignatureBuilder> typeInSignatureBuilders = new();

        public TypesInSignatureBuilder RentTypeInSigBuilder() => typeInSignatureBuilders.TryTake(out var result) ? result : new(this);
        public void ReturnTypeInSigBuilder(TypesInSignatureBuilder builder) {
            builder.Clear();
            typeInSignatureBuilders.Add(builder);
        }

        private TypeEntityMap(PackOptions options, IMetadataResolver mdResolver, IMetadataResolver externalMdResolver, DiagnosticTranslator translator) {
            Options = options;
            MdResolver = mdResolver;
            ExternalMdResolver = externalMdResolver;
            Diagnostics = translator;

            if (options.Parallelize) {
                throw new InvalidOperationException("Cannot parallelize at present");
            }
        }

        public static TypeEntityMap CreateForAllTypes(IEnumerable<ModuleDefinition> modules,
            PackOptions options, IMetadataResolver mdResolver,
            IMetadataResolver externalResolver, DiagnosticTranslator translator
        ) {
            var map = new TypeEntityMap(options, mdResolver, externalResolver, translator);
            var entsByName = new Dictionary<NullableUtf8String, Dictionary<NullableUtf8String, List<TypeEntity>>>();

            foreach (var module in modules) {
                foreach (var type in module.TopLevelTypes) {
                    map.InitType(type, entsByName);
                }
            }

            // reebuild entsByName into entitiesByName
            map.entitiesByName.EnsureCapacity(entsByName.Count);
            foreach (var kvp in entsByName) {
                var innerDict = new Dictionary<NullableUtf8String, UnifiedTypeEntity>(kvp.Value.Count);
                foreach (var kvp2 in kvp.Value) {
                    innerDict.Add(kvp2.Key, new(map, kvp2.Value));
                }
                map.entitiesByName.Add(kvp.Key, innerDict);
            }

            return map;
        }

        private void InitType(TypeDefinition def, Dictionary<NullableUtf8String, Dictionary<NullableUtf8String, List<TypeEntity>>> entsByName) {
            var entity = new TypeEntity(this, def);
            entityMap.Add(def, entity);

            if (def.DeclaringType is null) {
                if (!entsByName.TryGetValue(def.Namespace, out var nsDict)) {
                    entsByName.Add(def.Namespace, nsDict = new());
                }

                if (!nsDict.TryGetValue(def.Name, out var withName)) {
                    nsDict.Add(def.Name, withName = new());
                }

                withName.Add(entity);
            }

            // visit child types
            foreach (var type in def.NestedTypes) {
                InitType(type, entsByName);
            }
        }

        public TypeEntity Lookup(TypeDefinition def) => entityMap[def];
        public bool TryLookup(TypeDefinition def, [MaybeNullWhen(false)] out TypeEntity result)
            => entityMap.TryGetValue(def, out result);

        private readonly ConcurrentDictionary<ITypeDefOrRef, TypeEntityBase> defOrRefCache = new();
        private readonly ConcurrentDictionary<(object? scopeEntity, Utf8String? ns, Utf8String? name), ExternalTypeEntity> referenceEntities = new();
        public TypeEntityBase GetEntity(ITypeDefOrRef defOrRef) {
            return defOrRefCache.GetOrAdd(defOrRef, static (defOrRef, @this) => {
                if (defOrRef is TypeDefinition def && @this.TryLookup(def, out var result)) {
                    return result;
                }

                var resolved = @this.MdResolver.ResolveType(defOrRef);
                if (resolved is not null && @this.TryLookup(resolved, out result)) {
                    return result;
                }

                var resolvedDef = defOrRef as TypeDefinition;
                resolvedDef ??= @this.ExternalMdResolver.ResolveType(defOrRef);

                if (resolvedDef is null) {
                    @this.Diagnostics.ReportDiagnostic(ErrorCode.ERR_CouldNotResolveExternalReference, defOrRef);
                }

                var obj = resolvedDef?.Module?.Assembly?.ToString() ?? defOrRef.Scope?.GetAssembly()?.ToString();

                return @this.referenceEntities.GetOrAdd((obj, defOrRef.Namespace, defOrRef.Name),
                    static (k, t)
                        => new ExternalTypeEntity(t.@this, t.defOrRef), (@this, defOrRef));
            }, this);
        }

        public UnifiedTypeEntity ByName(Utf8String? @namespace, Utf8String? name)
            => entitiesByName[@namespace][name];

        public IEnumerable<UnifiedTypeEntity> EnumerateUnifiedTypeEntities() {
            foreach (var v1 in entitiesByName.Values) {
                foreach (var v2 in v1.Values) {
                    yield return v2;
                }
            }
        }

        private readonly ConcurrentDictionary<IHasCustomAttribute, TypeMergeMode?> cachedTypeMergeModes = new();

        public TypeMergeMode? GetTypeMergeMode(IHasCustomAttribute attrProv) {
            return cachedTypeMergeModes.GetOrAdd(attrProv, static (attrProv, @this) => {
                var result = attrProv.GetDeclaredMergeMode();
                if (result is null && attrProv is not ModuleDefinition and not AssemblyDefinition and IModuleProvider { Module: { } module }) {
                    result = @this.GetTypeMergeMode(module);
                    attrProv = module;
                }
                if (result is null && attrProv is ModuleDefinition { Assembly: { } assembly }) {
                    result = @this.GetTypeMergeMode(assembly);
                }
                return result;
            }, this);
        }

        private readonly ConcurrentDictionary<IHasCustomAttribute, BaseTypeMergeMode?> cachedBaseTypeMergeModes = new();

        public BaseTypeMergeMode? GetBaseTypeMergeMode(IHasCustomAttribute attrProv) {
            return cachedBaseTypeMergeModes.GetOrAdd(attrProv, static (attrProv, @this) => {
                var result = attrProv.GetDeclaredBaseMergeMode();
                if (result is null && attrProv is IModuleProvider { Module: { } module }) {
                    result = @this.GetBaseTypeMergeMode(module);
                    attrProv = module;
                }
                if (result is null && attrProv is ModuleDefinition { Assembly: { } assembly }) {
                    result = @this.GetBaseTypeMergeMode(assembly);
                }
                return result;
            }, this);
        }
    }
}
