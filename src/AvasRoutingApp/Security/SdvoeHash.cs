using System;
using System.Reflection;
using AvasRoutingApp.Logging;

namespace AvasRoutingApp.Security
{
    /// <summary>
    /// Provides device activation key generation and validation for SDVoE hardware.
    /// Implements offline HashEncode and CheckHashKey algorithms for air-gapped clinical deployments.
    /// Corresponds to VOIPS_LIB.SDVoEHash in Advantech runtime libraries.
    /// </summary>
    public static class SdvoeHash
    {
        public const string KEY_STR = "AVTechSDVoE";
        public const string SN_KEY = "AdvanTech";

        private static readonly Func<string, string, string>? _encodeDelegate;
        private static readonly Func<string, string, string, bool>? _checkDelegate;
        private static readonly Func<string, string>? _decodeDelegate;

        static SdvoeHash()
        {
            try
            {
                var asm = typeof(VOIPS_LIB.THUMBNAIL).Assembly;
                var hashType = asm.GetType("VOIPS_LIB.SDVoEHash");
                if (hashType != null)
                {
                    var mEncode = hashType.GetMethod("Hash_EnCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (mEncode != null)
                    {
                        _encodeDelegate = (Func<string, string, string>)Delegate.CreateDelegate(typeof(Func<string, string, string>), mEncode);
                    }

                    var mCheck = hashType.GetMethod("Check_HashKey", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (mCheck != null)
                    {
                        _checkDelegate = (Func<string, string, string, bool>)Delegate.CreateDelegate(typeof(Func<string, string, string, bool>), mCheck);
                    }

                    var mDecode = hashType.GetMethod("Hash_DeCode", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                    if (mDecode != null)
                    {
                        _decodeDelegate = (Func<string, string>)Delegate.CreateDelegate(typeof(Func<string, string>), mDecode);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("SdvoeHash", $"Failed to bind SDVoEHash delegate methods: {ex.Message}");
            }
        }

        /// <summary>
        /// Generates a 16-byte hex hash string (comma-delimited) from the device MAC address and serial number.
        /// e.g. "e8,0c,0d,de,fc,ee,b4,c3,19,e0,0d,20,ef,02,e1,80"
        /// </summary>
        public static string HashEncode(string mac, string sn)
        {
            if (string.IsNullOrWhiteSpace(mac))
            {
                throw new ArgumentException("MAC address cannot be null or empty.", nameof(mac));
            }
            if (string.IsNullOrWhiteSpace(sn))
            {
                throw new ArgumentException("Serial number cannot be null or empty.", nameof(sn));
            }

            if (_encodeDelegate != null)
            {
                return _encodeDelegate(mac, sn);
            }

            throw new InvalidOperationException("SDVoEHash encoder delegate is unavailable.");
        }

        /// <summary>
        /// Validates whether the provided authorization hash key matches the given MAC address and serial number.
        /// </summary>
        public static bool CheckHashKey(string mac, string sn, string checkHash)
        {
            if (string.IsNullOrWhiteSpace(mac) || string.IsNullOrWhiteSpace(sn) || string.IsNullOrWhiteSpace(checkHash))
            {
                return false;
            }

            if (_checkDelegate != null)
            {
                return _checkDelegate(mac, sn, checkHash);
            }

            return false;
        }

        /// <summary>
        /// Decodes a previously generated 16-byte hex hash string back to its raw components.
        /// </summary>
        public static string HashDecode(string checkHash)
        {
            if (string.IsNullOrWhiteSpace(checkHash))
            {
                return string.Empty;
            }

            if (_decodeDelegate != null)
            {
                return _decodeDelegate(checkHash);
            }

            return string.Empty;
        }
    }
}
