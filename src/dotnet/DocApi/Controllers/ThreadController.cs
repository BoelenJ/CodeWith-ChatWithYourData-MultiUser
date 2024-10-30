using Domain;
using Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Data;
using System.Text;
using DocApi.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Identity.Web.Resource;

namespace DocApi.Controllers
{
   
    [Route("threads")]
    [Authorize]
    [ApiController]
    [RequiredScope("chat")]

    public class ThreadController : ControllerBase
    {
        private readonly IThreadRepository _threadRepository;
        private readonly ILogger<ThreadController> _logger;
        private readonly IConfiguration _configuration;
        private readonly Kernel _kernel;
        private readonly VectorStoreTextSearch<IndexDoc> _search;
        private readonly PromptHelper _promptHelper;

        public class MessageRequest
        {
            public string Message { get; set; }
        }

        public ThreadController(
            ILogger<ThreadController> logger,
            IThreadRepository cosmosThreadRepository,
            IConfiguration configuration,
            Kernel kernel,
            VectorStoreTextSearch<IndexDoc> search,
            PromptHelper promptHelper
            )
        {
            _threadRepository = cosmosThreadRepository;
            _configuration = configuration;
            _logger = logger;
            _kernel = kernel;
            _search = search;
            _promptHelper = promptHelper;
        }

        [HttpGet("")]
        public async Task<IActionResult> GetThreads()
        {

            string userId = HttpContext.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

            if (userId == null)
            {
                return BadRequest();
            }

            _logger.LogInformation("Fetching threads from CosmosDb for userId : {0}", userId);
            
            List<Domain.Thread> threads = await _threadRepository.GetThreadsAsync(userId);

            _logger.LogInformation("Fetched threads from CosmosDb for userId : {0}", userId);
            return Ok(threads);
        }

        [HttpPost("")]
        public async Task<IActionResult> CreateThread()
        {

            string userId = HttpContext.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

            if (userId == null)
            {
                return BadRequest();
            }
            _logger.LogInformation("Creating thread in CosmosDb for userId : {0}", userId);

            Domain.Thread thread = await _threadRepository.CreateThreadAsync(userId);

            if(thread == null)
            {
                _logger.LogInformation("Failed to create thread in CosmosDb for userId : {0}", userId);
            }

            _logger.LogInformation("Created thread in CosmosDb for userId : {0}", userId);
            
            await _threadRepository.PostMessageAsync(userId, thread.Id, "You are a helpful assistant that helps people find information.", "system");

            return Ok(thread);
        }

        [HttpDelete("{threadId}")]
        public async Task<IActionResult> DeleteThread([FromRoute] string threadId)
        {
            _logger.LogInformation("Deleting thread in CosmosDb for threadId : {0}", threadId);

            string userId = HttpContext.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

            if (userId == null)
            {
                return BadRequest();
            }

            bool result = await _threadRepository.MarkThreadAsDeletedAsync(userId, threadId);

            if (result)
            {
                return Ok();
            } 

            return BadRequest();
           
        }

        [HttpGet("{threadId}/messages")]
        public async Task<IActionResult> Get([FromRoute] string threadId)
        {
            _logger.LogInformation("Fetching thread messages from CosmosDb for threadId : {0}", threadId);
            string userId = HttpContext.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

            if (userId == null)
            {
                return BadRequest();
            }

            List<ThreadMessage> result = await _threadRepository.GetMessagesAsync(userId, threadId);
            return Ok(result);
        }

        [HttpPost("{threadId}/messages")]
        [Produces("text/event-stream")]
        [Consumes("application/json")]
        public async Task<IActionResult> Post([FromRoute] string threadId, [FromBody] MessageRequest messageRequest)
        {
            _logger.LogInformation("Adding thread message to CosmosDb for threadId : {0}", threadId);

            string userId = HttpContext.User.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;

            if(userId == null)
            {
                return BadRequest();
            }

            List<ThreadMessage> messages = await _threadRepository.GetMessagesAsync(userId, threadId);

            ChatHistory history = _promptHelper.BuildConversationHistory(messages, messageRequest.Message);
           
            await _threadRepository.PostMessageAsync(userId, threadId, messageRequest.Message, "user");

            IChatCompletionService completionService = _kernel.GetRequiredService<IChatCompletionService>();

            string rewrittenQuery = await _promptHelper.RewriteQueryAsync(history);

            // Text search.
            var filter = new TextSearchFilter().Equality("ThreadId", threadId);
            var searchOptions = new TextSearchOptions() { 
                Filter = filter,
                Top = 3
            };
            KernelSearchResults<object> searchResults = await _search.GetSearchResultsAsync(rewrittenQuery, searchOptions);

            await _promptHelper.AugmentHistoryWithSearchResults(history, searchResults);

            var response = completionService.GetStreamingChatMessageContentsAsync(
                chatHistory: history,
                kernel: _kernel
            );

            var assistantResponse = "";

            await using (StreamWriter streamWriter = new StreamWriter(Response.Body, Encoding.UTF8))
            {
                await foreach (var chunk in response)
                {
                    assistantResponse += chunk.Content;
                    await streamWriter.WriteAsync(chunk.Content);
                    await streamWriter.FlushAsync();
                }
            }
            
            await _threadRepository.PostMessageAsync(userId, threadId, assistantResponse, "assistant");
            
            return new EmptyResult();
        }
    }
}
