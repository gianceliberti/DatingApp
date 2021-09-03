using System;
using System.Linq;
using System.Threading.Tasks;
using API.DTOs;
using API.Entities;
using API.Extensions;
using API.Interfaces;
using AutoMapper;
using Microsoft.AspNetCore.SignalR;

namespace API.SignalR {
    public class MessageHub : Hub {
        private readonly IMessageRepository messageRepository;
        private readonly IMapper mapper;
        private readonly IUserRepository userRepository;
        private readonly IHubContext<PresenceHub> presenceHub;
        private readonly PresenceTracker tracker;

        public MessageHub (IMessageRepository messageRepository, IMapper mapper,
            IUserRepository userRepository, IHubContext<PresenceHub> presenceHub, PresenceTracker tracker) 
            {
            this.tracker = tracker;
            this.presenceHub = presenceHub;
            this.userRepository = userRepository;
            this.mapper = mapper;
            this.messageRepository = messageRepository;
        }

        public override async Task OnConnectedAsync () {
            var HttpContext = Context.GetHttpContext ();
            var otherUser = HttpContext.Request.Query["user"].ToString ();
            var groupName = GetGroupName (Context.User.GetUsername (), otherUser);
            await Groups.AddToGroupAsync (Context.ConnectionId, groupName);
            var group = await AddToGroup (groupName);
            await Clients.Group(groupName).SendAsync("UpdatedGroup", group);

            var messages = await this.messageRepository.
            GetMessageThread (Context.User.GetUsername (), otherUser);
            await Clients.Caller.SendAsync ("ReceiveMessageThread", messages);
        }

        public override async Task OnDisconnectedAsync (Exception exception) {
            var group = await RemoveFromMessageGroup ();
            await Clients.Group(group.Name).SendAsync("UpdatedGroup", group);
            await base.OnDisconnectedAsync (exception);
        }

        public async Task SendMessage (createMessageDto createMessageDto) {
            var username = Context.User.GetUsername ();

            if (username == createMessageDto.RecipientUsername.ToLower ())
                throw new HubException ("You cannot send messages to yourself");

            var sender = await this.userRepository.GetUserByUsernameAsync (username);
            var recipient = await this.userRepository.GetUserByUsernameAsync (createMessageDto.RecipientUsername);

            if (recipient == null) throw new HubException ("Not found user");

            var message = new Message {
                Sender = sender,
                Recipient = recipient,
                SenderUsername = sender.UserName,
                RecipientUsername = recipient.UserName,
                Content = createMessageDto.Content
            };

            var groupName = GetGroupName (sender.UserName, recipient.UserName);

            var group = await this.messageRepository.GetMessageGroup (groupName);

            if (group.Connections.Any (x => x.Username == recipient.UserName)) 
            {
                message.DateRead = DateTime.UtcNow;
            } 
            else 
            {
                var connections = await this.tracker.GetConnectionsForUser(recipient.UserName);
                if (connections != null)
                {
                    await this.presenceHub.Clients.Clients(connections).SendAsync("NewMessageReceived",
                        new {username = sender.UserName, knownAs = sender.KnownAs});
                }
            }

            this.messageRepository.AddMessage (message);

            if (await this.messageRepository.SaveAllAsync ()) {
                await Clients.Group (groupName).SendAsync ("NewMessage", this.mapper.Map<MessageDto> (message));
            }

        }

        private async Task<Group> AddToGroup (string groupName) {
            var group = await this.messageRepository.GetMessageGroup (groupName);
            var connection = new Connection (Context.ConnectionId, Context.User.GetUsername ());

            if (group == null) {
                group = new Group (groupName);
                this.messageRepository.AddGroup (group);
            }

            group.Connections.Add (connection);

            if (await this.messageRepository.SaveAllAsync ()) return group;

            throw new HubException("Failed to join group");
        }

        private async Task<Group> RemoveFromMessageGroup ()
         {
            var group = await this.messageRepository.GetGroupForConnection (Context.ConnectionId);
            var connection = group.Connections.FirstOrDefault(x => x.ConnectionId == Context.ConnectionId);
            this.messageRepository.RemoveConnection (connection);
            if (await this.messageRepository.SaveAllAsync ()) return group;

            throw new HubException("Failed to remove from group");
        }

        private string GetGroupName (string caller, string other) {
            var stringCompare = string.CompareOrdinal (caller, other) < 0;
            return stringCompare ? $"{caller}-{other}" : $"{other}-{caller}";
        }
    }
}