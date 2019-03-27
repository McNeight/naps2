﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MimeKit;
using NAPS2.Util;

namespace NAPS2.ImportExport.Email.Oauth
{
    public class GmailEmailProvider : MimeEmailProvider
    {
        private readonly GmailOauthProvider gmailOauthProvider;

        public GmailEmailProvider(GmailOauthProvider gmailOauthProvider)
        {
            this.gmailOauthProvider = gmailOauthProvider;
        }
        
        protected override async Task SendMimeMessage(MimeMessage message, ProgressHandler progressCallback, CancellationToken cancelToken)
        {
            var messageId = await gmailOauthProvider.UploadDraft(message.ToString(), progressCallback, cancelToken);
            var userEmail = gmailOauthProvider.User;
            // Open the draft in the user's browser
            // Note: As of this writing, the direct url is bugged in the new gmail UI, and there is no workaround
            // https://issuetracker.google.com/issues/113127519
            // At least it directs to the drafts folder
            Process.Start($"https://mail.google.com/mail/?authuser={userEmail}#drafts/{messageId}");
        }
    }
}
