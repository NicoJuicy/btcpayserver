using System;
using Newtonsoft.Json;

namespace BTCPayServer.Client.Models
{
    public class ApplicationUserData
    {
        /// <summary>
        /// the id of the user
        /// </summary>
        public string Id { get; set; }

        /// <summary>
        /// the email AND username of the user
        /// </summary>
        public string Email { get; set; }
        
        /// <summary>
        /// the name of the user
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// the image url of the user
        /// </summary>
        public string ImageUrl { get; set; }

        /// <summary>
        /// Whether the user has verified their email
        /// </summary>
        public bool EmailConfirmed { get; set; }

        /// <summary>
        /// whether the user needed to verify their email on account creation
        /// </summary>
        public bool RequiresEmailConfirmation { get; set; }

        /// <summary>
        /// Whether the user was approved by an admin
        /// </summary>
        public bool Approved { get; set; }

        /// <summary>
        /// whether the user needed approval on account creation
        /// </summary>
        public bool RequiresApproval { get; set; }

        /// <summary>
        /// the roles of the user
        /// </summary>
        public string[] Roles { get; set; }

        /// <summary>
        /// the date the user was created. Null if created before v1.0.5.6.
        /// </summary>
        [JsonConverter(typeof(NBitcoin.JsonConverters.DateTimeToUnixTimeConverter))]
        public DateTimeOffset? Created { get; set; }

        public bool Disabled { get; set; }
    }
}
