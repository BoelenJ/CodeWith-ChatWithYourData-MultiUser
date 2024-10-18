import { useQuery } from "@tanstack/react-query";
import { useEffect, useState } from "react";
import { ChatService } from "../services/ChatService";
import { IChatMessage } from "../models/ChatMessage";

export const useChat = (chatId: string | undefined) => {

    const chatService = new ChatService();

    const [messages, setMessages] = useState<IChatMessage[]>([]);

    const { isPending: chatPending, error: chatError, data: chat } = useQuery({
        queryKey: ['chat', chatId],
        queryFn: async () => chatService.getChatAsync(chatId || ""),
        enabled: chatId != undefined,
        staleTime: 10000
    });

    useEffect(() => {
        if (chat) {
            if (chat.messages) {
                setMessages(chat.messages.filter(message => message.role !== 'system'));
            }
        }
    }, [chat])


    const sendMessage = async ({ message }: { message: string }) => {

        if(!chatId) return; 
        let result = '';
        setMessages(prev => {
            const updated = [...prev];
            updated.push({
                role: 'user',
                content: message
            },
                {
                    role: 'assistant',
                    content: result
                });
            return updated;
        });

        const response = await chatService.sendMessageAsync(chatId, message);
        
        if (!response || !response.body) {
            return;
        }

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        const loop = true;
        while (loop) {
            const { value, done } = await reader.read();
            if (done) {
                break;
            }
            const decodedChunk = decoder.decode(value, { stream: true });
            result += decodedChunk;
            setMessages(prev => {
                const updated = [...prev];
                updated[updated.length - 1] = {
                    role: 'assistant',
                    content: result
                };
                return updated;
            });
        }
    };

    return {
        chatPending,
        chatError,
        chat,
        messages,
        sendMessage
    };

}