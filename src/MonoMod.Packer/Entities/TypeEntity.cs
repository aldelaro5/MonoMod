﻿using AsmResolver;
using AsmResolver.DotNet;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;

namespace MonoMod.Packer.Entities {
    [DebuggerDisplay($"{{{nameof(DebuggerDisplay)}(),nq}}")]
    internal sealed class TypeEntity : TypeEntityBase {
        private string DebuggerDisplay() => Definition.FullName;

        public readonly TypeDefinition Definition;

        public TypeEntity(TypeEntityMap map, TypeDefinition def) : base(map) {
            Definition = def;
        }

        public override Utf8String? Namespace => Definition.Namespace;
        public override Utf8String? Name => Definition.Name;

        private MethodEntity CreateMethod(MethodDefinition m) => new(Map, m);
        private FieldEntity CreateField(FieldDefinition f) => new(Map, f);

        public new ImmutableArray<MethodEntity> StaticMethods => base.StaticMethods.CastArray<MethodEntity>();
        protected override ImmutableArray<MethodEntityBase> MakeStaticMethods() {
            return Definition.Methods
                        .Where(m => m.IsStatic)
                        .Select(CreateMethod)
                        .ToImmutableArray()
                        .CastArray<MethodEntityBase>();
        }

        public new ImmutableArray<MethodEntity> InstanceMethods => base.InstanceMethods.CastArray<MethodEntity>();
        protected override ImmutableArray<MethodEntityBase> MakeInstanceMethods() {
            return Definition.Methods
                            .Where(m => !m.IsStatic)
                            .Select(CreateMethod)
                            .ToImmutableArray()
                            .CastArray<MethodEntityBase>();
        }

        public new ImmutableArray<FieldEntity> StaticFields => base.StaticFields.CastArray<FieldEntity>();
        protected override ImmutableArray<FieldEntityBase> MakeStaticFields() {
            return Definition.Fields
                            .Where(f => f.IsStatic)
                            .Select(CreateField)
                            .ToImmutableArray()
                            .CastArray<FieldEntityBase>();
        }

        public new ImmutableArray<FieldEntity> InstanceFields => base.InstanceFields.CastArray<FieldEntity>();
        protected override ImmutableArray<FieldEntityBase> MakeInstanceFields() {
            return Definition.Fields
                            .Where(f => !f.IsStatic)
                            .Select(CreateField)
                            .ToImmutableArray()
                            .CastArray<FieldEntityBase>();
        }

        public new ImmutableArray<TypeEntity> NestedTypes => base.NestedTypes.CastArray<TypeEntity>();
        protected override ImmutableArray<TypeEntityBase> MakeNestedTypes() {
            return Definition.NestedTypes
                            .Select(Map.Lookup)
                            .ToImmutableArray()
                            .CastArray<TypeEntityBase>();
        }
    }
}
