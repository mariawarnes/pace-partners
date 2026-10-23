using System;
using System.Linq;
using System.Reflection;

namespace PacePartners.Mod.Infrastructure
{
    public enum SelectedCourseProbeStatus
    {
        Selected,
        GtBikeVNotLoaded,
        NoCourseSelected,
        IncompatibleGtBikeV,
        Error
    }

    public sealed class SelectedCourseSelection
    {
        public SelectedCourseSelection(
            ushort effectiveId,
            ushort baseId,
            string name,
            bool reverse,
            bool loop)
        {
            EffectiveId = effectiveId;
            BaseId = baseId;
            Name = name ?? string.Empty;
            Reverse = reverse;
            Loop = loop;
        }

        public ushort EffectiveId { get; }
        public ushort BaseId { get; }
        public string Name { get; }
        public bool Reverse { get; }
        public bool Loop { get; }
    }

    public sealed class SelectedCourseProbeResult
    {
        private SelectedCourseProbeResult(
            SelectedCourseProbeStatus status,
            SelectedCourseSelection selection,
            string error)
        {
            Status = status;
            Selection = selection;
            Error = error;
        }

        public SelectedCourseProbeStatus Status { get; }
        public SelectedCourseSelection Selection { get; }
        public string Error { get; }
        public bool IsSelected => Status == SelectedCourseProbeStatus.Selected && Selection != null;

        public static SelectedCourseProbeResult Success(SelectedCourseSelection selection)
        {
            if (selection == null) throw new ArgumentNullException(nameof(selection));
            return new SelectedCourseProbeResult(SelectedCourseProbeStatus.Selected, selection, null);
        }

        public static SelectedCourseProbeResult Failure(SelectedCourseProbeStatus status, string error)
        {
            if (status == SelectedCourseProbeStatus.Selected)
                throw new ArgumentException("A selected result must include a selection.", nameof(status));
            return new SelectedCourseProbeResult(status, null, error ?? "Course selection is unavailable.");
        }
    }

    /// <summary>
    /// Reads GTBikeV's authoritative static multiplayer course state. It has no
    /// geometry/proximity fallback and therefore fails closed when GTBikeV's
    /// runtime contract is absent or incompatible.
    /// </summary>
    public sealed class SelectedCourseProbe
    {
        private const string GtBikeAssemblyName = "GTBikeV";
        private const string MultiplayerTypeName = "GTBikeV.Network.Multiplayer";

        public SelectedCourseProbeResult Probe()
        {
            try
            {
                Assembly gtBikeAssembly = AppDomain.CurrentDomain
                    .GetAssemblies()
                    .FirstOrDefault(assembly =>
                        string.Equals(
                            assembly.GetName().Name,
                            GtBikeAssemblyName,
                            StringComparison.OrdinalIgnoreCase));
                if (gtBikeAssembly == null)
                {
                    return SelectedCourseProbeResult.Failure(
                        SelectedCourseProbeStatus.GtBikeVNotLoaded,
                        "GTBikeV.dll is not loaded in the current script AppDomain.");
                }

                Type multiplayerType = gtBikeAssembly.GetType(MultiplayerTypeName, false);
                if (multiplayerType == null)
                {
                    return Incompatible(
                        $"Loaded GTBikeV does not expose {MultiplayerTypeName}.");
                }

                const BindingFlags staticFlags =
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
                PropertyInfo currentCourseProperty =
                    multiplayerType.GetProperty("CurrentCourse", staticFlags);
                if (currentCourseProperty == null ||
                    currentCourseProperty.GetIndexParameters().Length != 0)
                {
                    return Incompatible(
                        $"{MultiplayerTypeName}.CurrentCourse is unavailable.");
                }

                object course = currentCourseProperty.GetValue(null, null);
                if (course == null)
                {
                    return SelectedCourseProbeResult.Failure(
                        SelectedCourseProbeStatus.NoCourseSelected,
                        "GTBikeV is loaded, but Multiplayer.CurrentCourse is null.");
                }

                object effectiveIdValue = ReadRequiredMember(course, "Id");
                object nameValue = ReadRequiredMember(course, "Name");
                object reverseValue = ReadRequiredMember(course, "Reverse");
                object loopValue = ReadRequiredMember(course, "Loop");
                if (effectiveIdValue == null ||
                    nameValue == null ||
                    reverseValue == null ||
                    loopValue == null)
                {
                    return Incompatible(
                        "GTBikeV's Course shape is incompatible; expected Id, Name, Reverse and Loop.");
                }

                ushort effectiveId = Convert.ToUInt16(effectiveIdValue);
                ushort baseId = (ushort)(effectiveId & 0x7fff);
                return SelectedCourseProbeResult.Success(
                    new SelectedCourseSelection(
                        effectiveId,
                        baseId,
                        Convert.ToString(nameValue),
                        Convert.ToBoolean(reverseValue),
                        Convert.ToBoolean(loopValue)));
            }
            catch (TargetInvocationException ex)
            {
                Exception cause = ex.InnerException ?? ex;
                return SelectedCourseProbeResult.Failure(
                    SelectedCourseProbeStatus.Error,
                    "GTBikeV rejected the selected-course probe: " + cause.Message);
            }
            catch (Exception ex)
            {
                return SelectedCourseProbeResult.Failure(
                    SelectedCourseProbeStatus.Error,
                    "Failed to read GTBikeV's selected course: " + ex.Message);
            }
        }

        private static SelectedCourseProbeResult Incompatible(string message)
        {
            return SelectedCourseProbeResult.Failure(
                SelectedCourseProbeStatus.IncompatibleGtBikeV,
                message);
        }

        private static object ReadRequiredMember(object instance, string name)
        {
            Type type = instance.GetType();
            const BindingFlags flags =
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

            PropertyInfo property = type.GetProperty(name, flags);
            if (property != null && property.GetIndexParameters().Length == 0)
                return property.GetValue(instance, null);

            FieldInfo field = type.GetField(name, flags);
            return field?.GetValue(instance);
        }
    }
}
