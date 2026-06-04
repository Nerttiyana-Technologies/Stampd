TEMPLATE_ID=$(curl -s $BASE_URL/api/templates/ \
  -H "Authorization: Bearer $TOKEN" | jq -r '.[0].id')

echo "Latest template: $TEMPLATE_ID"

curl -s $BASE_URL/api/templates/$TEMPLATE_ID \
  -H "Authorization: Bearer $TOKEN" | jq
