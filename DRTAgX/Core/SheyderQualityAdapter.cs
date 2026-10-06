using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Client;

namespace DRTAgX;

/// <summary>Runtime caps through private config views; original objects and JSON remain owned by Sheyder.</summary>
internal sealed class SheyderQualityAdapter : IDisposable
{
    private readonly List<ConfigView> _views = new();

    private static readonly Func<object, object> Clone = (Func<object, object>)typeof(object)
        .GetMethod("MemberwiseClone", BindingFlags.Instance | BindingFlags.NonPublic)!.CreateDelegate(typeof(Func<object, object>));

    internal SheyderQualityAdapter(ICoreClientAPI api)
    {
        try
        {
            object? system = api.ModLoader.GetModSystem("SheyderMod.SheyderModSystem");
            if (system == null) return;
            void Wrap(object renderer)
            {
                string? name = renderer.GetType().FullName;
                string? effect = name switch {
                    "SheyderMod.Features.GTAO.GtaoRenderer" => "Gtao",
                    "SheyderMod.Features.WaterShader.WaterSsrRenderer" => "Water", _ => null };
                if (effect == null) return;
                FieldInfo? field = AccessTools.Field(renderer.GetType(), "_getConfig");
                if (field?.GetValue(renderer) is not Delegate getter) return;
                foreach (var existing in _views) if (ReferenceEquals(existing.Owner, renderer)) return;
                var view = new ConfigView(renderer, field, getter, effect);
                _views.Add(view);
                field.SetValue(renderer, view.Wrapped);
            }
            if (AccessTools.Field(system.GetType(), "_renderers")?.GetValue(system) is IEnumerable renderers)
                foreach (object renderer in renderers) Wrap(renderer);
            // Captured Sheyder 1.1.3 registers water separately; it is absent from _renderers.
            // Wrap its config getter as well, while preserving the provider's object/JSON ownership.
            if (AccessTools.Field(system.GetType(), "_waterSsr")?.GetValue(system) is object water) Wrap(water);
            // Volumetric fog keeps Sheyder's configured steps and half-resolution
            // allocation in both modes. Lowering either reintroduces sparse shafts.
        }
        catch (Exception ex)
        {
            api.Logger.Warning("[DRTAgX] Optional quality caps retain the current path: " + ex.Message);
        }
    }

    private sealed class ConfigView
    {
        internal readonly object Owner;
        internal readonly FieldInfo Field;
        internal readonly Delegate Original, Wrapped;
        private readonly Func<object> _get;
        private readonly Func<object, object> _effect;
        private readonly Action<object, object> _publishEffect, _copy;
        private readonly Action<object> _cap;
        private object? _source, _sourceEffect, _loader, _cappedEffect;

        internal ConfigView(object owner, FieldInfo field, Delegate original, string effect)
        {
            Owner = owner; Field = field; Original = original;
            Type loaderType = original.GetType().GetMethod("Invoke")!.ReturnType;
            _get = Expression.Lambda<Func<object>>(Expression.Convert(Expression.Invoke(Expression.Constant(original)), typeof(object))).Compile();
            var obj = Expression.Parameter(typeof(object));
            PropertyInfo property = loaderType.GetProperty(effect) ?? throw new MissingMemberException(effect);
            Type effectType = property.PropertyType;
            _effect = Expression.Lambda<Func<object, object>>(Expression.Convert(Expression.Property(Expression.Convert(obj, loaderType), property), typeof(object)), obj).Compile();
            var value = Expression.Parameter(typeof(object));
            _publishEffect = Expression.Lambda<Action<object, object>>(Expression.Assign(Expression.Property(Expression.Convert(obj, loaderType), property), Expression.Convert(value, effectType)), obj, value).Compile();
            var source = Expression.Parameter(typeof(object));
            var copies = new List<Expression>();
            foreach (PropertyInfo p in effectType.GetProperties())
                if (p.CanRead && p.CanWrite && p.GetIndexParameters().Length == 0)
                    copies.Add(Expression.Assign(Expression.Property(Expression.Convert(obj, effectType), p), Expression.Property(Expression.Convert(source, effectType), p)));
            _copy = Expression.Lambda<Action<object, object>>(Expression.Block(copies), obj, source).Compile();
            var caps = new List<Expression>();
            void Cap(string name, int bound, bool floor = false)
            {
                var p = Expression.Property(Expression.Convert(obj, effectType), name);
                var math = typeof(Math).GetMethod(floor ? nameof(Math.Max) : nameof(Math.Min), new[] { typeof(int), typeof(int) })!;
                caps.Add(Expression.Assign(p, Expression.Call(math, p, Expression.Constant(bound))));
            }
            if (effect == "Gtao") { Cap("Slices", 2); Cap("Steps", 3); }
            if (effect == "Water") { Cap("Steps", 4); Cap("Downsample", 6, true); }
            _cap = Expression.Lambda<Action<object>>(Expression.Block(caps), obj).Compile();
            Wrapped = Expression.Lambda(original.GetType(), Expression.Convert(Expression.Call(Expression.Constant(this), nameof(Get), Type.EmptyTypes), loaderType)).Compile();
        }

        public object Get()
        {
            object original = _get();
            if (!FrameQuality.Current.Performance) return original;
            object effect = _effect(original);
            if (!ReferenceEquals(original, _source) || !ReferenceEquals(effect, _sourceEffect))
            {
                // Allocate only after a config replacement or first mode activation; scalar GUI edits copy without allocation.
                _source = original; _sourceEffect = effect; _loader = Clone(original); _cappedEffect = Clone(effect);
            }
            _copy(_cappedEffect!, effect); _cap(_cappedEffect!); _publishEffect(_loader!, _cappedEffect!);
            return _loader!;
        }
    }

    public void Dispose()
    {
        foreach (var view in _views)
            if (ReferenceEquals(view.Field.GetValue(view.Owner), view.Wrapped)) view.Field.SetValue(view.Owner, view.Original);
        _views.Clear();
    }
}
