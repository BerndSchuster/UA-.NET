/* Copyright (c) 1996-2016, OPC Foundation. All rights reserved.

   The source code in this file is covered under a dual-license scenario:
     - RCL: for OPC Foundation members in good-standing
     - GPL V2: everybody else

   RCL license terms accompanied with this source code. See http://opcfoundation.org/License/RCL/1.00/

   GNU General Public License as published by the Free Software Foundation;
   version 2 of the License are accompanied with this source code. See http://opcfoundation.org/License/GPLv2

   This source code is distributed in the hope that it will be useful,
   but WITHOUT ANY WARRANTY; without even the implied warranty of
   MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.
*/

using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;
using System.IdentityModel.Selectors;
using System.IdentityModel.Tokens;
using System.Security.Principal;
using System.Security.Cryptography.X509Certificates;
using System.ServiceModel.Security;
using System.Runtime.InteropServices;
using System.Threading;
using Opc.Ua;
using Opc.Ua.Server;

namespace AuthorizationServer
{
    /// <summary>
    /// Implements a basic Server.
    /// </summary>
    /// <remarks>
    /// Each server instance must have one instance of a StandardServer object which is
    /// responsible for reading the configuration file, creating the endpoints and dispatching
    /// incoming requests to the appropriate handler.
    /// 
    /// This sub-class specifies non-configurable metadata such as Product Name and initializes
    /// the DataTypesNodeManager which provides access to the data exposed by the Server.
    /// </remarks>
    public partial class AuthorizationServerServer : StandardServer
    {
        #region Private Fields
        private object m_lock = new object();
        private X509CertificateValidator m_certificateValidator;
        private UserTokenValidators m_validators;
        private Dictionary<uint, ImpersonationContext> m_contexts = new Dictionary<uint, ImpersonationContext>();

        private Dictionary<string, IList<NodeId>> m_scopes;
        private Dictionary<string, IList<NodeId>> m_users;
        private Dictionary<string, IList<NodeId>> m_roles;

        private NodeId PubSubNormalNodeId { get; set; }
        private NodeId PubSubSecureNodeId { get; set; }
        #endregion 

        public AuthorizationServerServer()
        {
        }

        #region Overridden Methods
        /// <summary>
        /// Initializes the server before it starts up.
        /// </summary>
        /// <remarks>
        /// This method is called before any startup processing occurs. The sub-class may update the 
        /// configuration object or do any other application specific startup tasks.
        /// </remarks>
        protected override void OnServerStarting(ApplicationConfiguration configuration)
        {
            m_validators = configuration.ParseExtension<UserTokenValidators>();

            if (m_validators != null)
            {
                foreach (var validator in m_validators)
                {
                    var hostname = System.Net.Dns.GetHostName().ToLower();

                    if (validator.AuthorityCertificate.SubjectName != null)
                    {
                        validator.AuthorityCertificate.SubjectName = validator.AuthorityCertificate.SubjectName.Replace("localhost", hostname);
                    }

                    var certificate = validator.AuthorityCertificate.Find(false);

                    if (certificate == null)
                    {
                        Utils.Trace("UserTokenValidators Certificate could not be found: {0}", certificate.SubjectName);
                    }

                    if (validator.IssuerUri == null)
                    {
                        validator.IssuerUri = Utils.GetApplicationUriFromCertficate(certificate);
                    }
                    else
                    {
                        validator.IssuerUri = validator.IssuerUri.Replace("localhost", hostname);
                    }

                    foreach (var policy in configuration.ServerConfiguration.UserTokenPolicies)
                    {
                        if (policy.PolicyId == validator.PolicyId)
                        {
                            policy.IssuerEndpointUrl = validator.IssuerEndpointUrl;
                            break;
                        }
                    }
                }
            }

            base.OnServerStarting(configuration);
            
            // it is up to the application to decide how to validate user identity tokens.
            // this function creates validators for X509 identity tokens.
            CreateUserIdentityValidators(configuration);

            m_scopes = new Dictionary<string, IList<NodeId>>();
            m_roles = new Dictionary<string, IList<NodeId>>();
            m_users = new Dictionary<string, IList<NodeId>>();
        }

        /// <summary>
        /// Called after the server has been started.
        /// </summary>
        protected override void OnServerStarted(IServerInternal server)
        {
            base.OnServerStarted(server);

            // request notifications when the user identity is changed. all valid users are accepted by default.
            server.SessionManager.ImpersonateUser += SessionManager_ImpersonateUser;

            // validate session-less requests.
            server.SessionManager.ValidateSessionLessRequest += SessionManager_ValidateSessionLessRequest;

            // set up mapping rules for scopes.
            m_scopes.Add("UAServer", new NodeId[] { ObjectIds.WellKnownRole_Observer });

            // set up mapping rules for known users.
            m_users.Add("gdsadmin", new NodeId[] { ObjectIds.WellKnownRole_SecurityAdmin });
            m_users.Add("appadmin", new NodeId[] { ObjectIds.WellKnownRole_Engineer });
            m_users.Add("appuser", new NodeId[] { ObjectIds.WellKnownRole_Operator });

            // set up mapping rules for known roles.
            m_roles.Add("admin", new NodeId[] { ObjectIds.WellKnownRole_SecurityAdmin, ObjectIds.WellKnownRole_ConfigureAdmin });
            m_roles.Add("superuser", new NodeId[] { ObjectIds.WellKnownRole_Engineer });
            m_roles.Add("user", new NodeId[] { ObjectIds.WellKnownRole_Operator });

            if (m_validators != null)
            {
                ServerInternal.DiagnosticsNodeManager.CreateAuthorizationService(ServerInternal.DefaultSystemContext, m_validators);
            }
        }

        /// <summary>
        /// Called before the server stops
        /// </summary>
        protected override void OnServerStopping()
        {
            base.OnServerStopping();
        }

        /// <summary>
        /// Creates the node managers for the server.
        /// </summary>
        protected override MasterNodeManager CreateMasterNodeManager(IServerInternal server, ApplicationConfiguration configuration)
        {
            Utils.Trace("Creating the Node Managers.");

            List<INodeManager> nodeManagers = new List<INodeManager>();

            // create the custom node managers.
            var nodeManager = new AuthorizationServerNodeManager(server, configuration);
            nodeManagers.Add(nodeManager);

            // create master node manager.
            return new MasterNodeManager(server, configuration, null, nodeManagers.ToArray());
        }

        /// <summary>
        /// Loads the non-configurable properties for the application.
        /// </summary>
        /// <remarks>
        /// These properties are exposed by the server but cannot be changed by administrators.
        /// </remarks>
        protected override ServerProperties LoadServerProperties()
        {
            ServerProperties properties = new ServerProperties();

            properties.ManufacturerName = "Some Company Inc";
            properties.ProductName      = "Authorization Server";
            properties.ProductUri       = "http://somecompany.com/AuthorizationServer";
            properties.SoftwareVersion  = Utils.GetAssemblySoftwareVersion();
            properties.BuildNumber      = Utils.GetAssemblyBuildNumber();
            properties.BuildDate        = Utils.GetAssemblyTimestamp();

            return properties;
        }

        /// <summary>
        /// This method is called at the being of the thread that processes a request.
        /// </summary>
        protected override OperationContext ValidateRequest(RequestHeader requestHeader, RequestType requestType)
        {
            OperationContext context = base.ValidateRequest(requestHeader, requestType);

            if (requestType == RequestType.Write)
            {
                // reject all writes if no user provided.
                if (context.UserIdentity.TokenType == UserTokenType.Anonymous)
                {
                    // construct translation object with default text.
                    TranslationInfo info = new TranslationInfo(
                        "NoWriteAllowed",
                        "en-US",
                        "Must provide a valid windows user before calling write.");

                    // create an exception with a vendor defined sub-code.
                    throw new ServiceResultException(new ServiceResult(
                        StatusCodes.BadUserAccessDenied,
                        "NoWriteAllowed",
                        Opc.Ua.Gds.Namespaces.OpcUaGds,
                        new LocalizedText(info)));
                }

                SecurityToken securityToken = context.UserIdentity.GetSecurityToken();

                // check for a user name token.
                UserNameSecurityToken userNameToken = securityToken as UserNameSecurityToken;

                if (userNameToken != null)
                {
                    ImpersonationContext impersonationContext = UserIdentity.LogonUser(userNameToken, false);

                    lock (m_lock)
                    {
                        m_contexts.Add(context.RequestId, impersonationContext);
                    }
                }
            }

            return context;
        }

        /// <summary>
        /// This method is called in a finally block at the end of request processing (i.e. called even on exception).
        /// </summary>
        protected override void OnRequestComplete(OperationContext context)
        {
            ImpersonationContext impersonationContext = null;

            lock (m_lock)
            {
                if (m_contexts.TryGetValue(context.RequestId, out impersonationContext))
                {
                    m_contexts.Remove(context.RequestId);
                }
            }

            if (impersonationContext != null)
            {
                impersonationContext.Dispose();
            }

            base.OnRequestComplete(context);
        }

        /// <summary>
        /// Creates the objects used to validate the user identity tokens supported by the server.
        /// </summary>
        private void CreateUserIdentityValidators(ApplicationConfiguration configuration)
        {
            for (int ii = 0; ii < configuration.ServerConfiguration.UserTokenPolicies.Count; ii++)
            {
                UserTokenPolicy policy = configuration.ServerConfiguration.UserTokenPolicies[ii];

                // create a validator for a certificate token policy.
                if (policy.TokenType == UserTokenType.Certificate)
                {
                    // the name of the element in the configuration file.
                    XmlQualifiedName qname = new XmlQualifiedName(policy.PolicyId, Opc.Ua.Namespaces.OpcUa);

                    // find the location of the trusted issuers.
                    CertificateTrustList trustedIssuers = configuration.ParseExtension<CertificateTrustList>(qname);

                    if (trustedIssuers == null)
                    {
                        Utils.Trace(
                            (int)Utils.TraceMasks.Error,
                            "Could not load CertificateTrustList for UserTokenPolicy {0}",
                            policy.PolicyId);

                        continue;
                    }

                    // trusts any certificate in the trusted people store.
                    m_certificateValidator = X509CertificateValidator.PeerTrust;
                }
            }
        }

        private IUserIdentity ValidateJwt(UserTokenPolicy policy, JwtEndpointParameters parameters, string jwt)
        {
            string issuerUri = null;
            X509Certificate2 authorityCertificate = null;

            if (m_validators != null)
            {
                foreach (var validator in m_validators)
                {
                    if (validator.PolicyId == policy.PolicyId)
                    {
                        authorityCertificate = validator.AuthorityCertificate.Find(false);
                        issuerUri = validator.IssuerUri;
                        break;
                    }
                }
            }

            IUserIdentity identity = JwtUtils.ValidateToken(new Uri(parameters.AuthorityUrl), authorityCertificate, issuerUri, Configuration.ApplicationUri, jwt);

            JwtSecurityToken jwtToken = identity.GetSecurityToken() as JwtSecurityToken;

            if (jwtToken == null)
            {
                throw new ServiceResultException(StatusCodes.BadInternalError);
            }

            List<NodeId> roles = new List<NodeId>();

            // valid token means the user has been authenticated.
            roles.Add(ObjectIds.WellKnownRole_AuthenticatedUser);

            // find additional roles based on the scopes in the role.
            foreach (var claim in jwtToken.Claims)
            {
                switch (claim.Type)
                {
                    case "scp":
                    {
                        var value = claim.Value.ToString().ToLowerInvariant();
                        var scopes = value.Split();

                        IList<NodeId> rolesForScope = null;

                        foreach (var scope in scopes)
                        {
                            if (m_scopes.TryGetValue(scope, out rolesForScope))
                            {
                                foreach (var roleForScope in rolesForScope)
                                {
                                    if (!roles.Contains(roleForScope))
                                    {
                                        roles.Add(roleForScope);
                                    }
                                }
                            }
                        }

                        break;
                    }
                }
            }

            return new RoleBasedIdentity(identity, roles);
        }

        private void SessionManager_ValidateSessionLessRequest(object sender, ValidateSessionLessRequestEventArgs e)
        {
            // check for encryption.
            var endpoint = SecureChannelContext.Current.EndpointDescription;

            if (endpoint == null || (endpoint.SecurityPolicyUri == SecurityPolicies.None && !endpoint.EndpointUrl.StartsWith(Uri.UriSchemeHttps)) || endpoint.SecurityMode == MessageSecurityMode.Sign)
            {
                e.Error = StatusCodes.BadSecurityModeInsufficient;
                return;
            }

            // find user token policy.
            UserTokenPolicy selectedPolicy = null;
            JwtEndpointParameters parameters = null;

            foreach (var policy in endpoint.UserIdentityTokens)
            {
                if (policy.IssuedTokenType == Opc.Ua.Profiles.JwtUserToken)
                {
                    parameters = new JwtEndpointParameters();
                    parameters.FromJson(policy.IssuerEndpointUrl);
                    selectedPolicy = policy;
                    break;
                }
            }

            if (parameters == null)
            {
                e.Error = StatusCodes.BadIdentityTokenRejected;
                return;
            }

            // check authentication token.
            if (NodeId.IsNull(e.AuthenticationToken) || e.AuthenticationToken.IdType != IdType.String || e.AuthenticationToken.NamespaceIndex != 0)
            {
                e.Error = StatusCodes.BadIdentityTokenInvalid;
                return;
            }

            // validate token.
            string jwt = (string)e.AuthenticationToken.Identifier;

            var identity = ValidateJwt(selectedPolicy, parameters, jwt);
            Utils.Trace("JSON Web Token Accepted: {0}", identity.DisplayName);

            e.Identity = identity;
            e.Error = ServiceResult.Good;
        }

        /// <summary>
        /// Called when a client tries to change its user identity.
        /// </summary>
        private void SessionManager_ImpersonateUser(Session session, ImpersonateEventArgs args)
        {
            // check for an issued token.
            IssuedIdentityToken issuedToken = args.NewIdentity as IssuedIdentityToken;

            if (issuedToken != null)
            {
                if (args.UserTokenPolicy.IssuedTokenType == Opc.Ua.Profiles.JwtUserToken)
                {
                    JwtEndpointParameters parameters = new JwtEndpointParameters();
                    parameters.FromJson(args.UserTokenPolicy.IssuerEndpointUrl);
                    var jwt = new UTF8Encoding().GetString(issuedToken.DecryptedTokenData);
                    var identity = ValidateJwt(args.UserTokenPolicy, parameters, jwt);
                    Utils.Trace("JSON Web Token Accepted: {0}", identity.DisplayName);
                    args.Identity = identity;
                    return;
                }
            }

            // check for a anonymous token.
            AnonymousIdentityToken anonymousToken = args.NewIdentity as AnonymousIdentityToken;

            if (anonymousToken != null)
            {
                var identity = new UserIdentity(anonymousToken);
                args.Identity = new RoleBasedIdentity(identity, new NodeId[] { ObjectIds.WellKnownRole_Anonymous });
                Utils.Trace("Anonymous Token Accepted: {0}", args.Identity.DisplayName);
                return;
            }

            throw new ServiceResultException(StatusCodes.BadIdentityTokenRejected);
        }

        /// <summary>
        /// Initializes the validator from the configuration for a token policy.
        /// </summary>
        /// <param name="issuerCertificate">The issuer certificate.</param>
        private SecurityTokenResolver CreateSecurityTokenResolver(CertificateIdentifier issuerCertificate)
        {
            if (issuerCertificate == null)
            {
                throw new ArgumentNullException("issuerCertificate");
            }

            // find the certificate.
            X509Certificate2 certificate = issuerCertificate.Find(false);

            if (certificate == null)
            {
                throw ServiceResultException.Create(
                    StatusCodes.BadCertificateInvalid,
                    "Could not find issuer certificate: {0}",
                    issuerCertificate);
            }

            // create a security token representing the certificate.
            List<SecurityToken> tokens = new List<SecurityToken>();
            tokens.Add(new X509SecurityToken(certificate));

            // create issued token resolver.
            SecurityTokenResolver tokenResolver = SecurityTokenResolver.CreateDefaultSecurityTokenResolver(
                new System.Collections.ObjectModel.ReadOnlyCollection<SecurityToken>(tokens),
                false);

            return tokenResolver;
        }
        
        /// <summary>
        /// Verifies that a certificate user token is trusted.
        /// </summary>
        private void VerifyCertificate(X509Certificate2 certificate)
        {
            try
            {
                m_certificateValidator.Validate(certificate);
            }
            catch (Exception e)
            {
                // construct translation object with default text.
                TranslationInfo info = new TranslationInfo(
                    "InvalidCertificate",
                    "en-US",
                    "'{0}' is not a trusted user certificate.",
                    certificate.Subject);

                // create an exception with a vendor defined sub-code.
                throw new ServiceResultException(new ServiceResult(
                    e,
                    StatusCodes.BadIdentityTokenRejected,
                    "InvalidCertificate",
                    Opc.Ua.Gds.Namespaces.OpcUaGds,
                    new LocalizedText(info)));
            }
        }

        /// <summary>
        /// Validates a Kerberos WSS user token.
        /// </summary>
        private SecurityToken ParseAndVerifyKerberosToken(byte[] tokenData)
        {
            XmlDocument document = new XmlDocument();
            XmlNodeReader reader = null;

            try
            {
                document.InnerXml = new UTF8Encoding().GetString(tokenData).Trim();
                reader = new XmlNodeReader(document.DocumentElement);

                SecurityToken securityToken = new WSSecurityTokenSerializer().ReadToken(reader, null);
                System.IdentityModel.Tokens.KerberosReceiverSecurityToken receiver = securityToken as KerberosReceiverSecurityToken;

                KerberosSecurityTokenAuthenticator authenticator = new KerberosSecurityTokenAuthenticator();

                if (authenticator.CanValidateToken(receiver))
                {
                    authenticator.ValidateToken(receiver);
                }

                return securityToken;
            }
            catch (Exception e)
            {
                // construct translation object with default text.
                TranslationInfo info = new TranslationInfo(
                    "InvalidKerberosToken",
                    "en-US",
                    "'{0}' is not a valid Kerberos token.",
                    document.DocumentElement.LocalName);

                // create an exception with a vendor defined sub-code.
                throw new ServiceResultException(new ServiceResult(
                    e,
                    StatusCodes.BadIdentityTokenRejected,
                    "InvalidKerberosToken",
                    Opc.Ua.Gds.Namespaces.OpcUaGds,
                    new LocalizedText(info)));
            }
            finally
            {
                if (reader != null)
                {
                    reader.Close();
                }
            }
        }
        #endregion
    }
}
