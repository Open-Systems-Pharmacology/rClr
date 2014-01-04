﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Rclr
{
    /// <summary>
    /// The main access point for R and rClr code to interact with the Common Language Runtime
    /// </summary>
    /// <remarks>
    /// The purpose of this class is to host gather method written in C# in preference to C code in rClr.c.
    /// </remarks>
    public static class ClrFacade
    {
        /// <summary>
        /// Invoke an instance method of an object
        /// </summary>
        public static object CallInstanceMethod(object obj, string methodName, object[] arguments)
        {
            object result = null;
            try
            {
                LastCallException = string.Empty;
                Type[] types = getTypes(arguments);
                BindingFlags bf = BindingFlags.Public | BindingFlags.Instance | BindingFlags.InvokeMethod;
                var classType = obj.GetType();
                MethodInfo method = findMethod(classType, methodName, bf, types);
                if (method != null)
                    result = invokeMethod(obj, arguments, method);
                else
                    throw new MissingMethodException(String.Format("Could not find method {0} on object", methodName));
            }
            catch (Exception ex)
            {
                if (!LogThroughR(ex))
                    throw;
            }
            return result;

        }

        /// <summary>
        /// Invokes a static method given the name of a type. Unique name to facilitate binding with Mono.
        /// </summary>
        public static object CallStaticMethodMono(string typename, string methodName, object[] arguments)
        {
             return CallStaticMethod(typename, methodName, arguments);
        }

        /// <summary>
        /// Invokes a static method given the name of a type.
        /// </summary>
        public static object CallStaticMethod(string typename, string methodName, object[] arguments)
        {
            object result = null;
            try
            {
                LastCallException = string.Empty;
                var t = GetType(typename);
                if (t == null)
                    throw new ArgumentException(String.Format("Type not found: {0}", typename));
                // In order to handle the R Date and POSIXt conversion, we have to standardise on UTC in the C layer. 
                // The CLR hosting API seems to only marshall to date-times to Unspecified (probably cannot do otherwise)
                // We need to make sure these are Utc DateTime at this point.
                arguments = makeDatesUtcKind(arguments);
                result = CallStaticMethod(t, methodName, arguments);
            }
            catch (Exception ex)
            {
                if (!LogThroughR(ex))
                    throw;
            }
            return result;
        }

        private static bool LogThroughR(Exception ex)
        {
            // Initially just wanted to print to R as below. HOWEVER
            // https://r2clr.codeplex.com/workitem/67
            // if (DataConverter != null)
            // {
            //     DataConverter.Error(FormatException(ex));
            //     return true;
            // }
            // Instead, using the following
            LastCallException = FormatException(ex);
            LastException = LastCallException;
            // Rely on this returning false so that caller rethrows the exception, so that 
            // we can retrieve the error in the C layer in the MS.NET related code.
            return false;
        }

        public static string FormatException(Exception ex)
        {
            Exception innermost = ex;
            while (innermost.InnerException != null)
                innermost = innermost.InnerException;

            // Note that if using Environment.NewLine below instead of "\n", the rgui prompt is losing it
            // Actually even with the latter it is, but less so. Annoying.
            var result = string.Format("Type:    {1}{0}Message: {2}{0}Method:  {3}{0}Stack trace:{0}{4}{0}{0}",
                "\n", innermost.GetType(), innermost.Message, innermost.TargetSite, innermost.StackTrace);
            // See whether this helps with the Rgui prompt:
            return result.Replace("\r\n", "\n");
        }

        /// <summary>
        /// Invoke a method on a type
        /// </summary>
        /// <param name="classType"></param>
        /// <param name="methodName"></param>
        /// <param name="arguments"></param>
        /// <returns></returns>
        public static object CallStaticMethod (Type classType, string methodName, object[] arguments)
        {
            if (arguments.GetType () == typeof(string[])) // workaround https://r2clr.codeplex.com/workitem/11
                arguments = new object[]{arguments};
            Type[] types = getTypes (arguments);
            // the following code was to test issues with Mono (). Cannot reproduce as of Sept 2013
            //for (int i = 0; i < types.Length; i++) {
            //    if (types [i] == null)
            //        try {
            //        string s = (string) arguments [i];
            //            Console.WriteLine ("arguments[i] = {0}", s);
            //            Console.WriteLine ("arguments[i].GetType() = {0}", arguments [i].GetType ());
            //        } catch (Exception ex) {
                        
            //        } finally {
            //            throw new NullReferenceException ("Type is a null reference at index " + i +
            //                " and arguments[i]==null is " + (arguments [i] == null).ToString ()
            //            );
            //        }
            //}
            BindingFlags bf = BindingFlags.Public | BindingFlags.Static | BindingFlags.InvokeMethod;
            var method = classType.GetMethod(methodName, bf, null, types, null);
            if (method == null)
                method = classType.GetMethod(methodName);
            if (method != null)
            {
                //if (method.GetParameters().Length == 1 && method.GetParameters()[0].ParameterType == typeof(object[]))
                //    arguments = new object[] { arguments }; // necessary for e.g. static void QueryTypes(params object[] blah)
                return invokeMethod(null, arguments, method);
            }
            else
                throw new MissingMethodException(String.Format("Could not find static method {0} on type {1}", methodName, classType.FullName));
        }

        /// <summary>
        /// Gets/sets a data converter to customize or extend the marshalling of data between R and the CLR
        /// </summary>
        public static IDataConverter DataConverter { get; set; }

        /// <summary>
        /// Gets if there is a custom data converter set on this facade
        /// </summary>
        public static bool DataConverterIsSet { get { return DataConverter != null; } }

        public static object WrapDataFrame(IntPtr pointer, int sexptype)
        {
            return (DataConverter == null ? null : DataConverter.ConvertFromR(pointer, sexptype));
        }

        /// <summary>
        /// Creates an instance of an object, given the type name
        /// </summary>
        public static object CreateInstance(string typename, params object[] arguments)
        {
            object result = null;
            try
            {
                LastCallException = string.Empty;

                var t = GetType(typename);
                if (t == null)
                    throw new ArgumentException(string.Format("Could not determine Type from string '{0}'", typename));
                result = ((arguments == null || arguments.Length == 0)
                                  ? Activator.CreateInstance(t)
                                  : Activator.CreateInstance(t, arguments));
            }
            catch (Exception ex)
            {
                if (!LogThroughR(ex))
                    throw;
            }
            return result;
        }

        public static Type GetObjectType(object obj)
        {
            return obj.GetType();
        }

        public static Type GetType(string typename)
        {
            if (string.IsNullOrEmpty(typename))
                throw new ArgumentException("missing type specification");

            var t = Type.GetType(typename);
            if (t == null)
            {
                var loadedAssemblies = System.AppDomain.CurrentDomain.GetAssemblies();
                var typeComponents = typename.Split(',');
                if (typeComponents.Length > 1) // "TheNamespace.TheShortTypeName,TheAssemblyName"
                {
                    string aName = typeComponents[typeComponents.Length - 1];
                    var assembly = loadedAssemblies.FirstOrDefault((x => x.GetName().Name == aName));
                    if (assembly == null)
                    {
                        Console.WriteLine(String.Format("Assembly not found: {0}", aName));
                        return null;
                    }
                    t = assembly.GetType(typeComponents[0]);
                }
                else // typeComponents.Length == 1
                {
                    // Then we only have something like "TheNamespace.TheShortTypeName", Need to parse all the assemblies.
                    string tName = typeComponents[0];
                    foreach (var item in loadedAssemblies)
                    {
                        var types = item.GetTypes();
                        t = types.FirstOrDefault((x => x.FullName == tName));
                        if ( t != null )
                            return t;
                    }
                }
                if (t == null)
                {
                    var msg = String.Format("Type not found: {0}", typename);
                    Console.WriteLine(msg);
                    return null;
                }
            }
            return t;
        }

        /// <summary>
        /// Gets the full name of a type.
        /// </summary>
        /// <remarks>For easier operations from the C code</remarks>
        public static string GetObjectTypeName(object obj)
        {
            var result = obj.GetType().FullName;
            return result;
        }

        /// <summary>
        /// Loads an assembly, using the Assembly.LoadFrom or Assembly.Load method depending on the argument
        /// </summary>
        public static Assembly LoadFrom(string pathOrAssemblyName)
        {
            Assembly result = null;
            if(File.Exists(pathOrAssemblyName))
                result = Assembly.LoadFrom(pathOrAssemblyName);
            else if (isFullyQualifiedAssemblyName(pathOrAssemblyName))
                result = Assembly.Load(pathOrAssemblyName);
            else
                // the use of LoadWithPartialName is deprecated, but this is highly convenient for the end user untill there is 
                // another safer and convenient alternative
#pragma warning disable 618, 612
                result = Assembly.LoadWithPartialName(pathOrAssemblyName);
#pragma warning restore 618, 612
            //Console.WriteLine(result.CodeBase);
            return result;
        }

        private static bool isFullyQualifiedAssemblyName(string p)
        {
            //"System.Windows.Presentation, Version=3.5.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089")            
            return p.Contains("PublicKeyToken=");
        }

        [Obsolete("This may be superseeded as this was swamping user with too long a list", false)]
        public static string[] GetMembers(object obj)
        {
            List<MemberInfo> members = new List<MemberInfo>();
            var classType = obj.GetType();
            members.AddRange(obj.GetType().GetMembers());
            var ifTypes = classType.GetInterfaces();
            for (int i = 0; i < ifTypes.Length; i++)
            {
                var t = ifTypes[i];
                members.AddRange(t.GetMembers());
            }
            var result = Array.ConvertAll(members.ToArray(), (x => x.Name));
            result = result.Distinct().ToArray();
            Array.Sort(result);
            return result;
        }

        /// <summary>
        /// Gets the numerical value of a Date in the R system. This is different from the origin of the CLI DateTime.
        /// </summary>
        public static double GetRDateDoubleRepresentation(DateTime date)
        {
            var tspan = GetUtcTimeSpanRorigin(ref date);
            var res = tspan.TotalDays;
            //            Console.WriteLine(res);
            return res;
        }

        private static TimeSpan GetUtcTimeSpanRorigin(ref DateTime date)
        {
            date = date.ToUniversalTime();
            //            Console.WriteLine("date: {0}", date); 
            var tspan = date - RDateOrigin; // POSIXct internal representation is always a linear scale with UTC origin RDateOrigin
            return tspan;
        }

        public static double GetRPosixCtDoubleRepresentation(DateTime date)
        {
            var tspan = GetUtcTimeSpanRorigin(ref date);
            var res = tspan.TotalSeconds;
            //            Console.WriteLine(res);
            return res;
        }

        /// <summary>
        /// Gets the numerical value of a Date in the R system. This is different from the origin of the CLI DateTime.
        /// </summary>
        public static double[] GetRDateDoubleRepresentations(DateTime[] dates)
        {
			var result = new double[dates.Length];
			for (int i = 0; i < dates.Length; i++) {
				result[i]=GetRDateDoubleRepresentation(dates[i]);
			}
            return result;
        }

        /// <summary>
        /// Gets the numerical value of a POSIXct in the R system. This is different from the origin of the CLI DateTime.
        /// </summary>
        public static double GetRDatePosixtcNumericValue(DateTime date)
        {
            // IMPORTANT: the default representation of POSIXct is local time (i.e. no time zone info)
            date = date.ToLocalTime();
            var dt = date - RDateOrigin;
            var res = dt.TotalSeconds;
            return res;
        }

        public static double[] GetRDatePosixtcNumericValues(DateTime[] dates)
        {
            var result = new double[dates.Length];
            for (int i = 0; i < dates.Length; i++)
            {
                result[i] = GetRDatePosixtcNumericValue(dates[i]);
            }
            return result;
        }

        public static DateTime[] DateTimeArrayToUtc(DateTime[] dateTimes)
        {
            var result = new DateTime[dateTimes.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = dateTimes[i].ToUniversalTime();
            return result;
        }

        /// <summary>
        /// Given the numerical representation of a date in R, return the equivalent CLI DateTime.
        /// </summary>
        /// <param name="rDateNumericValue">The numerical value in R, e.g. as.numeric(as.Date('2001-02-03'))</param>
        public static DateTime CreateDateFromREpoch(double rDateNumericValue)
        {
            var res = RDateOrigin + TimeSpan.FromDays(rDateNumericValue);
//            Console.WriteLine("dbl value: {0}", rDateNumericValue);
//            Console.WriteLine("dtime value: {0}", res.ToString());
            return res;
        }

        /// <summary>
        /// Given the numerical representation of a date in R, return the equivalent CLI DateTime.
        /// </summary>
        /// <param name="rDateNumericValue">The numerical value in R, e.g. as.numeric(as.POSIXct('2001-02-03'))</param>
        public static DateTime CreateDateFromRPOSIXct(double rDateNumericValue)
        {
            var res = RDateOrigin + TimeSpan.FromSeconds(rDateNumericValue);
//            Console.WriteLine("dbl value: {0}", rDateNumericValue);
//            Console.WriteLine("dtime value: {0}", res.ToString());
            return res;
        }

        /// <summary>
        /// Returns a string that represents the parameter passed.
        /// </summary>
        /// <remarks>This is useful e.g. to quickly check from R the CLR equivalent of an R POSIXt object</remarks>
        public static string ToString(object obj)
        {
            return obj.ToString();
        }

        public static DateTime[] CreateDateArrayFromREpoch(double[] rDateNumericValues)
        {
            return Array.ConvertAll(rDateNumericValues, CreateDateFromREpoch);
        }

		public static void SetFieldOrProperty (object obj, string name, object value)
		{
			if(obj == null) throw new ArgumentNullException();
			var b = BindingFlags.Public | BindingFlags.Instance;
			internalSetFieldOrProperty(obj.GetType(), name, b, obj, value);
		}

		public static void SetFieldOrProperty (Type type, string name, object value)
		{
			if(type == null) throw new ArgumentNullException();
			var b = BindingFlags.Public | BindingFlags.Static;
			internalSetFieldOrProperty(type, name, b, null, value);
		}

		public static void SetFieldOrProperty (string typename, string name, object value)
		{
            Type t = ClrFacade.GetType(typename);
			if (t == null)
                throw new ArgumentException(String.Format("Type not found: {0}", typename));
			SetFieldOrProperty(t, name, value);
		}

		static void internalSetFieldOrProperty (Type t, string name, BindingFlags b, object obj_or_null, object value)
		{
			var field = t.GetField (name, b);
			if (field == null) {
				var property = t.GetProperty (name, b);
				if (property == null)
					throw new ArgumentException (string.Format ("Public instance field or property name {0} not found", name));
				else
					property.SetValue (obj_or_null, value, null);
			}
			else
				field.SetValue (obj_or_null, value);
		}


        public static object GetFieldOrProperty (string typename, string name)
		{
            Type t = ClrFacade.GetType(typename);
			if (t == null)
                throw new ArgumentException(String.Format("Type not found: {0}", typename));
			return GetFieldOrPropertyType(t, name);
        }

        public static object GetFieldOrProperty (object obj, string name)
		{
			var b = BindingFlags.Public | BindingFlags.Instance;
			Type t = obj.GetType ();
			return internalGetFieldOrProperty (t, name, b, obj);
		}

        private static object GetFieldOrPropertyType(Type type, string name)
		{
			var b = BindingFlags.Public | BindingFlags.Static;
			return internalGetFieldOrProperty (type, name, b, null);
		}

		static object internalGetFieldOrProperty (Type t, string name, BindingFlags b, object obj_or_null)
		{
			var field = t.GetField (name, b);
			if (field == null) {
				var property = t.GetProperty (name, b);
				if (property == null)
					throw new ArgumentException (string.Format ("Public instance field or property name '{0}' not found", name));
				else
					return property.GetValue (obj_or_null, null);
			}
			else
				return field.GetValue (obj_or_null);
		}
        /// <summary>
        /// A default binder for finding methods; a placeholder for a way to customize or refine the method selection process for rClr.
        /// </summary>
        private static Binder methodBinder = System.Type.DefaultBinder;

        private static MethodInfo findMethod(Type classType, string methodName, BindingFlags bf, Type[] types)
        {
            return ReflectionHelper.GetMethod(classType, methodName, methodBinder, bf, types);
        }

        private static object invokeMethod(object obj, object[] arguments, MethodInfo method)
        {
            var parameters = method.GetParameters();
            var numParameters = parameters.Length;
            if (numParameters > arguments.Length)
            {
                // Assume this is because of parameters with default values, and handle as per:
                // http://msdn.microsoft.com/en-us/library/x0acewhc.aspx
                var newargs = new object[numParameters];
                arguments.CopyTo(newargs, 0);
                for (int i = arguments.Length; i < newargs.Length; i++)
                    newargs[i] = Type.Missing;
                arguments = newargs;
            }
            else if (parameters.Length > 0)
            {
                // check whether we have a method with the last argument with a 'params' keyword
                // This is not handled magically when using reflection.
                var p = parameters[parameters.Length - 1];
                if (p.GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0)
                    arguments = packParameters(arguments, numParameters, p);
            }
            return marshallData(method.Invoke(obj, arguments));
        }

        private static object[] packParameters(object[] arguments, int np, ParameterInfo p)
        {
            var arrayType = p.ParameterType;
            if (np < 1)
                throw new ArgumentException("numParameters must be strictly positive");
            if (!arrayType.IsArray)
                throw new ArgumentException("Inconsistent - arguments should not be packed with a non-array method parameter");
            return PackParameters(arguments, np, arrayType);
        }

        public static object[] PackParameters(object[] arguments, int np, Type arrayType)
        {
            // f(obj, string, params int[] integers) // numParameters = 3
            int na = arguments.Length;
            var tElement = arrayType.GetElementType(); // Int32 for an array int[]
            var result = new object[np];
            Array.Copy(arguments, result, np - 1); // obj, string
            if ((np == na) && (arrayType == arguments[na - 1].GetType()))
            {
                // we already have an int[] pre-packed. 
                // {obj, "methName", new int[]{p1, p2, p3})  length 3
                    // NOTE Possible singular and ambiguous cases: params object[] or params Array[]
                    Array.Copy(arguments, na - 1, result, na - 1, 1);
            }
            else
            {
                // {obj, "methName", p1, p2, p3)  length 5
                Array paramParam = Array.CreateInstance(tElement, na - np + 1); // na - np + 1 = 5 - 3 + 1 = 3
                Array.Copy(arguments, np - 1, paramParam, 0, na - np + 1); // np - 1 = 3 - 1 = 2 start index
                result.SetValue(paramParam, np - 1);
            }
            return result;
        }

        private static object marshallData(object obj)
        {
            obj = conditionDateTime(obj);
            return (DataConverter != null ? DataConverter.ConvertToR(obj) : obj);
        }

        private static object[] makeDatesUtcKind(object[] arguments)
        {
            object[] newArgs = (object[])arguments.Clone();
            for (int i = 0; i < arguments.Length; i++)
            {
                var obj = arguments[i];
                if (obj is DateTime)
                    newArgs[i] = forceUtcKind((DateTime)obj);
                else if (obj is DateTime[])
                    newArgs[i] = forceUtcKind((DateTime[])obj);
            }
            return newArgs;
        }

        private static DateTime[] forceUtcKind(DateTime[] dateTimes)
        {
            var result = new DateTime[dateTimes.Length];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = forceUtcKind(dateTimes[i]);
            }
            return result;
        }

        private static DateTime forceUtcKind(DateTime dateTime)
        {
            return new DateTime(dateTime.Ticks, DateTimeKind.Utc);
        }

        private static object conditionDateTime(object obj)
        {
            // 2013-05: move to have date-time in R as POSIXct objects. 
            // For reliability, only support UTC until specs are refined.
            // See unit tests in test-datetime.r
            if (obj == null) return obj;
            if (obj.GetType() == typeof(DateTime))
                return ((DateTime)obj).ToUniversalTime();
            else if (obj.GetType() == typeof(DateTime[]))
                return DateTimeArrayToUtc((DateTime[])obj);
            else
                return obj;
        }

        private static readonly DateTime RDateOrigin = new DateTime(1970,1,1);

        private static Type[] getTypes(object[] arguments)
        {
            // var result = Array.ConvertAll(arguments, (x => (x == null ? typeof(object) : x.GetType())));
            var result = new Type[arguments.Length];
            for (int i = 0; i < arguments.Length; i++) {
                result[i] = (arguments[i] == null ? typeof(object) : arguments[i].GetType ());
            }
            return result;
        }

        // Work around https://r2clr.codeplex.com/workitem/67

        /// <summary>
        /// A transient property with the printable format of the innermost exception of the latest clrCall[...] call.
        /// </summary>
        public static string LastCallException { get; private set; }

        /// <summary>
        /// A property with the printable format of the innermost exception of the last failed clrCall[...] call.
        /// </summary>
        public static string LastException { get; private set; }

    }
}
