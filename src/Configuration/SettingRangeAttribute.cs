using System;

#nullable disable

namespace CompostBin
{
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class SettingRangeAttribute : Attribute
    {
        public double Min
        {
            get;
        }
        public double Max
        {
            get;
        }
        public SettingRangeAttribute(double min, double max)
        {
            Min = min;
            Max = max;
        }
    }
}
