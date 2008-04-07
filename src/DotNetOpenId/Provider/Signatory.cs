using System;
using System.Collections;
using System.Collections.Specialized;
using System.Text;
using System.Security.Cryptography;
using System.Collections.Generic;
using IProviderAssociationStore = DotNetOpenId.IAssociationStore<DotNetOpenId.AssociationRelyingPartyType>;
using System.Diagnostics;

namespace DotNetOpenId.Provider {
	/// <summary>
	/// Signs things.
	/// </summary>
	internal class Signatory {
		/// <summary>
		/// The duration any association and secret key the Provider generates will be good for.
		/// </summary>
		static readonly TimeSpan smartAssociationLifetime = TimeSpan.FromDays(14);
		/// <summary>
		/// The duration a secret key used for signing dumb client requests will be good for.
		/// </summary>
		static readonly TimeSpan dumbSecretLifetime = TimeSpan.FromMinutes(5);

		/// <summary>
		/// The store for shared secrets.
		/// </summary>
		IProviderAssociationStore store;

		public Signatory(IProviderAssociationStore store) {
			if (store == null)
				throw new ArgumentNullException("store");

			this.store = store;
		}

		public void Sign(EncodableResponse response) {
			if (TraceUtil.Switch.TraceInfo) {
				Trace.TraceInformation("Digitally sign the response.");
			}

			Association assoc;
			string assoc_handle = response.PreferredAssociationHandle;

			if (!string.IsNullOrEmpty(assoc_handle)) {
				assoc = GetAssociation(assoc_handle, AssociationRelyingPartyType.Smart);

				if (assoc == null) {
					if (TraceUtil.Switch.TraceInfo) {
						Trace.TraceInformation("No associaton found with assoc_handle. Setting invalidate_handle and creating new Association.");
					}

					response.Fields[response.Protocol.openidnp.invalidate_handle] = assoc_handle;
					assoc = CreateAssociation(AssociationRelyingPartyType.Dumb, null);
				}
			} else {
				assoc = this.CreateAssociation(AssociationRelyingPartyType.Dumb, null);
				if (TraceUtil.Switch.TraceInfo) {
					Trace.TraceInformation("No assoc_handle supplied. Creating new association.");
				}
			}

			response.Fields[response.Protocol.openidnp.assoc_handle] = assoc.Handle;
			response.Signed.Add(response.Protocol.openidnp.assoc_handle);

			response.Fields[response.Protocol.openidnp.signed] = String.Join(",", response.Signed.ToArray());
			response.Fields[response.Protocol.openidnp.sig] =
				Convert.ToBase64String(assoc.Sign(response.Fields, response.Signed, string.Empty));

			if (TraceUtil.Switch.TraceInfo) {
				Trace.TraceInformation("Digital signature successfully created");
			}
		}

		public virtual bool Verify(string assoc_handle, string signature, IDictionary<string, string> signed_pairs, IList<string> signedKeyOrder) {
			if (TraceUtil.Switch.TraceInfo) {
				Trace.TraceInformation("Start signature verification for assoc_handle = '{0}'", assoc_handle);
			}

			Association assoc = GetAssociation(assoc_handle, AssociationRelyingPartyType.Dumb);

			string expected_sig;

			if (assoc == null) {
				if (TraceUtil.Switch.TraceInfo) {
					Trace.TraceInformation("End signature verification. Signature verification failed. No matching association handle found ");
				}

				return false;
			} else {
				if (TraceUtil.Switch.TraceInfo) {
					Trace.TraceInformation("Found matching association handle. ");
				}
				if (TraceUtil.Switch.TraceVerbose) {
					Trace.TraceInformation(assoc.ToString());
				}
			}

			if (TraceUtil.Switch.TraceInfo) {
				Trace.TraceInformation("Matching association found ");
			}

			expected_sig = Convert.ToBase64String(assoc.Sign(signed_pairs, signedKeyOrder));

			if (TraceUtil.Switch.TraceInfo) {
				Trace.TraceInformation("Expected signature is '{0}'. Actual signature is '{1}' ", expected_sig, signature);

				if (signature == expected_sig) {
					Trace.TraceInformation("End signature verification. Signature verification passed");
				} else {
					Trace.TraceInformation("End signature verification. Signature verification failed");
				}
			}

			return expected_sig.Equals(signature, StringComparison.Ordinal);
		}

		public virtual Association CreateAssociation(AssociationRelyingPartyType associationType, OpenIdProvider provider) {
			if (provider == null && associationType == AssociationRelyingPartyType.Smart) 
				throw new ArgumentNullException("provider", "For Smart associations, the provider must be given.");

			if (TraceUtil.Switch.TraceInfo) {
				Trace.TraceInformation("Start Create Association. Association type = {0}", associationType);
			}

			bool useSha256;
			string assoc_type;
			if (associationType == AssociationRelyingPartyType.Dumb) {
				useSha256 = true;
				assoc_type = Protocol.v20.Args.SignatureAlgorithm.HMAC_SHA256;
			} else {
				assoc_type = Util.GetRequiredArg(provider.Query, provider.Protocol.openid.assoc_type);
				Debug.Assert(Array.IndexOf(provider.Protocol.Args.SignatureAlgorithm.All, assoc_type) >= 0, "This should have been checked by our caller.");
				useSha256 = assoc_type.Equals(provider.Protocol.Args.SignatureAlgorithm.HMAC_SHA256, StringComparison.Ordinal);
			}
			int hashSize = useSha256 ? CryptUtil.Sha256.HashSize : CryptUtil.Sha1.HashSize;

			RNGCryptoServiceProvider generator = new RNGCryptoServiceProvider();
			byte[] secret = new byte[hashSize / 8];
			byte[] uniq_bytes = new byte[4];
			string uniq;
			string handle;
			Association assoc;

			generator.GetBytes(secret);
			generator.GetBytes(uniq_bytes);

			uniq = Convert.ToBase64String(uniq_bytes);

			double seconds = DateTime.UtcNow.Subtract(Association.UnixEpoch).TotalSeconds;

			handle = "{{" + assoc_type + "}{" + seconds + "}{" + uniq + "}";

			TimeSpan lifeSpan = associationType == AssociationRelyingPartyType.Dumb ? dumbSecretLifetime : smartAssociationLifetime;
			assoc = useSha256 ? (Association)
				new HmacSha256Association(handle, secret, lifeSpan) :
				new HmacSha1Association(handle, secret, lifeSpan);

			store.StoreAssociation(associationType, assoc);

			if (TraceUtil.Switch.TraceInfo) {
				Trace.TraceInformation("End Create Association. Association successfully created. key = '{0}', handle = '{1}' ", associationType, handle);
			}

			return assoc;
		}

		public virtual Association GetAssociation(string assoc_handle, AssociationRelyingPartyType associationType) {
			if (TraceUtil.Switch.TraceInfo) {
				Trace.TraceInformation("Start get association from store '{0}'.", assoc_handle);
			}

			if (assoc_handle == null)
				throw new ArgumentNullException("assoc_handle");

			Association assoc = store.GetAssociation(associationType, assoc_handle);
			if (assoc == null || assoc.IsExpired) {
				Trace.TraceInformation("Association expired or not in store. Trying to remove association if it still exists.");
				store.RemoveAssociation(associationType, assoc_handle);
				assoc = null;
			}

			if (TraceUtil.Switch.TraceInfo) {
				Trace.TraceInformation("End get association from store '{0}'. Association found? =  {1}", assoc_handle, (assoc != null).ToString());
			}

			return assoc;
		}

		public virtual void Invalidate(string assoc_handle, AssociationRelyingPartyType associationType) {
			if (TraceUtil.Switch.TraceInfo) {
				Trace.TraceInformation("Start invalidate association '{0}'.", assoc_handle);
			}

			store.RemoveAssociation(associationType, assoc_handle);

			if (TraceUtil.Switch.TraceInfo) {
				Trace.TraceInformation("End invalidate association '{0}'.", assoc_handle);
			}
		}
	}
}
